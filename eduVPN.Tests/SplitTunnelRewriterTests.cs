/*
    eduVPN - VPN for education and research

    Copyright: 2024-2026 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

using eduEx;
using eduVPN.Models;
using eduVPN.ViewModels.VPN;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace eduVPN.Tests
{
    [TestClass()]
    public class SplitTunnelRewriterTests
    {
        #region Fixtures

        /// <summary>
        /// A full-tunnel OpenVPN configuration shaped like the ones eduVPN servers hand out,
        /// including the inline blocks which must survive byte for byte.
        /// </summary>
        private const string FullTunnelOvpn =
            "client\r\n" +
            "dev tun\r\n" +
            "remote vpn.example.org 1194 udp\r\n" +
            "remote-cert-tls server\r\n" +
            "verify-x509-name vpn.example.org name\r\n" +
            "data-ciphers AES-256-GCM\r\n" +
            "auth none\r\n" +
            "<ca>\r\n" +
            "-----BEGIN CERTIFICATE-----\r\n" +
            "# this line only looks like a comment and must be kept\r\n" +
            "route 10.0.0.0 255.0.0.0\r\n" +
            "-----END CERTIFICATE-----\r\n" +
            "</ca>\r\n" +
            "<cert>\r\n" +
            "-----BEGIN CERTIFICATE-----\r\n" +
            "-----END CERTIFICATE-----\r\n" +
            "</cert>\r\n" +
            "<key>\r\n" +
            "-----BEGIN PRIVATE KEY-----\r\n" +
            "-----END PRIVATE KEY-----\r\n" +
            "</key>\r\n" +
            "<tls-crypt>\r\n" +
            "-----BEGIN OpenVPN Static key V1-----\r\n" +
            "-----END OpenVPN Static key V1-----\r\n" +
            "</tls-crypt>\r\n";

        /// <summary>
        /// The same profile, but with the routes written into the client configuration rather than
        /// pushed by the server.
        /// </summary>
        private const string LocalRoutesOvpn =
            "client\r\n" +
            "redirect-gateway def1 ipv6\r\n" +
            "route 10.0.0.0 255.0.0.0\r\n" +
            "route-ipv6 2001:db8::/32\r\n" +
            "route-gateway 10.43.0.1\r\n" +
            "dhcp-option DNS 10.43.0.1\r\n" +
            "<ca>\r\n" +
            "redirect-gateway def1\r\n" +
            "</ca>\r\n";

        private static readonly List<IPPrefix> FullTunnelAllowedIPs =
            new List<IPPrefix> { new IPPrefix("0.0.0.0/0"), new IPPrefix("::/0") };

        private static readonly List<IPPrefix> TunnelPrefixes =
            new List<IPPrefix> { new IPPrefix("10.43.0.2/24"), new IPPrefix("fd43::2/112") };

        private static SplitTunnelSettings Settings(
            SplitTunnelMode mode,
            params string[] routes)
        {
            return new SplitTunnelSettings
            {
                Mode = mode,
                Routes = routes.ToList(),
            };
        }

        private static List<string> Texts(IEnumerable<IPPrefix> prefixes)
        {
            return prefixes.Select(IPPrefixMath.ToCidrString).ToList();
        }

        #endregion

        #region WireGuard

        [TestMethod()]
        public void OffLeavesAllowedIPsUntouched()
        {
            var result = SplitTunnelRewriter.Apply(
                FullTunnelAllowedIPs, SplitTunnelSettings.Off, true, TunnelPrefixes,
                new IPPrefix("145.100.1.1/32"));
            CollectionAssert.AreEqual(new[] { "0.0.0.0/0", "::/0" }, Texts(result));
        }

        [TestMethod()]
        public void IncludeOnlyReplacesAllowedIPsOnADefaultGatewayProfile()
        {
            var result = SplitTunnelRewriter.Apply(
                FullTunnelAllowedIPs,
                Settings(SplitTunnelMode.IncludeOnly, "10.0.0.0/8", "130.89.0.0/16"),
                true,
                TunnelPrefixes,
                new IPPrefix("145.100.1.1/32"));
            // The interface prefixes are added back so the tunnel subnet stays reachable. The
            // tunnel's own /24 is swallowed by the user's 10.0.0.0/8.
            CollectionAssert.AreEqual(
                new[] { "10.0.0.0/8", "130.89.0.0/16", "fd43::/112" },
                Texts(result));
        }

        [TestMethod()]
        public void IncludeOnlyIntersectsOnANonDefaultGatewayProfile()
        {
            var result = SplitTunnelRewriter.Apply(
                new[] { new IPPrefix("130.89.0.0/16"), new IPPrefix("145.100.0.0/16") },
                Settings(SplitTunnelMode.IncludeOnly, "130.89.0.0/24", "10.0.0.0/8"),
                false,
                new[] { new IPPrefix("10.43.0.2/24") });
            // 10.0.0.0/8 was never pushed, so it is not added; the /24 narrows the pushed /16.
            CollectionAssert.AreEqual(new[] { "10.43.0.0/24", "130.89.0.0/24" }, Texts(result));
        }

        [TestMethod()]
        public void ExcludeComputesTheComplement()
        {
            var result = SplitTunnelRewriter.Apply(
                FullTunnelAllowedIPs,
                Settings(SplitTunnelMode.Exclude, "192.168.0.0/16"),
                true,
                TunnelPrefixes,
                new IPPrefix("145.100.1.1/32"));
            var texts = Texts(result);
            Assert.IsFalse(texts.Contains("0.0.0.0/0"));
            Assert.IsFalse(texts.Contains("192.168.0.0/16"));
            // IPv6 was not touched, so it stays fully tunnelled.
            Assert.IsTrue(texts.Contains("::/0"));
            // The excluded LAN and the peer endpoint are really gone, not merely absent as a
            // literal prefix.
            Assert.IsFalse(result.Any(p => IPPrefixMath.Contains(p, new IPPrefix("192.168.0.0/16"))));
            Assert.IsFalse(result.Any(p => IPPrefixMath.Contains(p, new IPPrefix("145.100.1.1/32"))));
        }

        [TestMethod()]
        public void ExcludeMatchesTheReferenceComplement()
        {
            var excluded = new IPPrefix("192.168.0.0/16");
            var result = SplitTunnelRewriter.Apply(
                new[] { new IPPrefix("0.0.0.0/0") },
                Settings(SplitTunnelMode.Exclude, "192.168.0.0/16"),
                true);
            // Reference implementation: every /16 of the IPv4 space except the excluded one must be
            // covered exactly once.
            var covered = 0;
            for (var i = 0; i < 65536; i++)
            {
                var probe = new IPPrefix(
                    new System.Net.IPAddress(new byte[] { (byte)(i >> 8), (byte)(i & 0xff), 0, 0 }), 16);
                var hits = result.Count(p => IPPrefixMath.Contains(p, probe));
                if (probe.Equals(excluded))
                    Assert.AreEqual(0, hits, "The excluded network must not be covered");
                else
                {
                    Assert.AreEqual(1, hits, "Every other /16 must be covered exactly once");
                    covered++;
                }
            }
            Assert.AreEqual(65535, covered);
        }

        [TestMethod()]
        public void TunnelSubnetSurvivesBeingExcluded()
        {
            var result = SplitTunnelRewriter.Apply(
                FullTunnelAllowedIPs,
                Settings(SplitTunnelMode.Exclude, "10.43.0.0/24"),
                true,
                TunnelPrefixes);
            Assert.IsTrue(result.Any(p => IPPrefixMath.Contains(p, new IPPrefix("10.43.0.2/32"))));
        }

        [TestMethod()]
        public void InvalidRoutesAreIgnoredRatherThanFatal()
        {
            var result = SplitTunnelRewriter.Apply(
                FullTunnelAllowedIPs,
                Settings(SplitTunnelMode.IncludeOnly, "10.0.0.0/8", "nonsense", "999.0.0.0/8", "0.0.0.0/0"),
                true);
            CollectionAssert.AreEqual(new[] { "10.0.0.0/8" }, Texts(result));
        }

        [TestMethod()]
        public void EndpointPrefixOnlyAcceptsLiterals()
        {
            Assert.AreEqual("145.100.1.1/32", IPPrefixMath.ToCidrString(SplitTunnelRewriter.EndpointPrefix("145.100.1.1")));
            Assert.AreEqual("2001:db8::1/128", IPPrefixMath.ToCidrString(SplitTunnelRewriter.EndpointPrefix("2001:db8::1")));
            Assert.IsNull(SplitTunnelRewriter.EndpointPrefix("vpn.example.org"));
            Assert.IsNull(SplitTunnelRewriter.EndpointPrefix(null));
            Assert.IsNull(SplitTunnelRewriter.EndpointPrefix("  "));
        }

        [TestMethod()]
        public void ApplyIsPure()
        {
            var settings = Settings(SplitTunnelMode.Exclude, "192.168.0.0/16");
            var first = SplitTunnelRewriter.Apply(FullTunnelAllowedIPs, settings, true, TunnelPrefixes);
            var second = SplitTunnelRewriter.Apply(FullTunnelAllowedIPs, settings, true, TunnelPrefixes);
            CollectionAssert.AreEqual(Texts(first), Texts(second));
            // The input list is never mutated.
            CollectionAssert.AreEqual(new[] { "0.0.0.0/0", "::/0" }, Texts(FullTunnelAllowedIPs));
        }

        #endregion

        #region OpenVPN

        [TestMethod()]
        public void OffReturnsTheConfigurationUnchanged()
        {
            var result = SplitTunnelRewriter.ApplyOpenVPN(FullTunnelOvpn, SplitTunnelSettings.Off, true);
            // Reference equality, so the generated .ovpn is byte-identical to a build without the
            // feature.
            Assert.AreSame(FullTunnelOvpn, result);
        }

        [TestMethod()]
        public void IncludeOnlyDirectives()
        {
            var directives = SplitTunnelRewriter.Directives(
                Settings(SplitTunnelMode.IncludeOnly, "10.0.0.0/8", "130.89.0.0/16"), true);
            CollectionAssert.AreEqual(
                new[]
                {
                    "pull-filter ignore \"redirect-gateway\"",
                    "pull-filter ignore \"redirect-private\"",
                    "pull-filter ignore \"route \"",
                    "pull-filter ignore \"route-ipv6 \"",
                    "route 10.0.0.0 255.0.0.0 vpn_gateway",
                    "route 130.89.0.0 255.255.0.0 vpn_gateway",
                },
                directives);
        }

        [TestMethod()]
        public void ExcludeDirectives()
        {
            var directives = SplitTunnelRewriter.Directives(
                Settings(SplitTunnelMode.Exclude, "192.168.0.0/16", "2001:db8::/32"), true);
            CollectionAssert.AreEqual(
                new[]
                {
                    "redirect-gateway def1 ipv6",
                    "route 192.168.0.0 255.255.0.0 net_gateway",
                    "route-ipv6 2001:db8::/32 net_gateway",
                },
                directives);
        }

        [TestMethod()]
        public void ExcludeOnANonDefaultGatewayProfileDoesNotForceADefaultRoute()
        {
            var directives = SplitTunnelRewriter.Directives(
                Settings(SplitTunnelMode.Exclude, "192.168.0.0/16"), false);
            CollectionAssert.AreEqual(
                new[] { "route 192.168.0.0 255.255.0.0 net_gateway" },
                directives);
        }

        [TestMethod()]
        public void RouteFilterCannotMatchRouteGateway()
        {
            var directives = SplitTunnelRewriter.Directives(
                Settings(SplitTunnelMode.IncludeOnly, "10.0.0.0/8"), true);
            // The trailing space is what keeps pushed route-gateway and route-metric options.
            Assert.IsTrue(directives.Contains("pull-filter ignore \"route \""));
            Assert.IsFalse(directives.Contains("pull-filter ignore \"route\""));
        }

        [TestMethod()]
        public void DnsSystemDropsThePushedResolvers()
        {
            var directives = SplitTunnelRewriter.Directives(
                new SplitTunnelSettings { Dns = SplitTunnelDnsMode.System }, true);
            CollectionAssert.AreEqual(
                new[] { "pull-filter ignore \"dhcp-option DNS\"" },
                directives);
        }

        [TestMethod()]
        public void DnsServerAndSplitEmitNothing()
        {
            foreach (var dns in new[] { SplitTunnelDnsMode.Server, SplitTunnelDnsMode.Split })
                Assert.AreEqual(
                    0,
                    SplitTunnelRewriter.Directives(new SplitTunnelSettings { Dns = dns }, true).Count);
        }

        [TestMethod()]
        public void ApplyOpenVPNPreservesInlineBlocksByteForByte()
        {
            var result = SplitTunnelRewriter.ApplyOpenVPN(
                LocalRoutesOvpn, Settings(SplitTunnelMode.IncludeOnly, "10.0.0.0/8"), true);
            // The redirect-gateway inside <ca> only looks like an option and must be left alone.
            Assert.IsTrue(result.Contains("<ca>\r\nredirect-gateway def1\r\n</ca>"));
            foreach (var block in new[] { "<ca>", "</ca>" })
                Assert.AreEqual(1, Occurrences(result, block), block);
        }

        [TestMethod()]
        public void IncludeOnlyCommentsOutLocalRoutes()
        {
            var result = SplitTunnelRewriter.ApplyOpenVPN(
                LocalRoutesOvpn, Settings(SplitTunnelMode.IncludeOnly, "10.0.0.0/8"), true);
            var lines = result.Split(new[] { "\r\n" }, StringSplitOptions.None);
            // pull-filter only drops pushed options, so the local ones are commented out.
            Assert.IsTrue(lines.Contains("# split-tunnel: redirect-gateway def1 ipv6"));
            Assert.IsTrue(lines.Contains("# split-tunnel: route 10.0.0.0 255.0.0.0"));
            Assert.IsTrue(lines.Contains("# split-tunnel: route-ipv6 2001:db8::/32"));
            // route-gateway and the pushed DNS are not route options and must stay.
            Assert.IsTrue(lines.Contains("route-gateway 10.43.0.1"));
            Assert.IsTrue(lines.Contains("dhcp-option DNS 10.43.0.1"));
            // ...and our own routes are appended.
            Assert.IsTrue(lines.Contains("route 10.0.0.0 255.0.0.0 vpn_gateway"));
        }

        [TestMethod()]
        public void ExcludeKeepsLocalRoutes()
        {
            var result = SplitTunnelRewriter.ApplyOpenVPN(
                LocalRoutesOvpn, Settings(SplitTunnelMode.Exclude, "192.168.0.0/16"), true);
            var lines = result.Split(new[] { "\r\n" }, StringSplitOptions.None);
            // EXCLUDE wants the full tunnel, so nothing is removed.
            Assert.IsTrue(lines.Contains("redirect-gateway def1 ipv6"));
            Assert.IsTrue(lines.Contains("route 10.0.0.0 255.0.0.0"));
            Assert.IsTrue(lines.Contains("route 192.168.0.0 255.255.0.0 net_gateway"));
        }

        [TestMethod()]
        public void DnsSystemCommentsOutLocalDnsOptions()
        {
            var result = SplitTunnelRewriter.ApplyOpenVPN(
                LocalRoutesOvpn,
                new SplitTunnelSettings { Dns = SplitTunnelDnsMode.System },
                true);
            var lines = result.Split(new[] { "\r\n" }, StringSplitOptions.None);
            Assert.IsTrue(lines.Contains("# split-tunnel: dhcp-option DNS 10.43.0.1"));
            Assert.IsTrue(lines.Contains("pull-filter ignore \"dhcp-option DNS\""));
            // The mode is Off, so the routes are untouched.
            Assert.IsTrue(lines.Contains("redirect-gateway def1 ipv6"));
        }

        [TestMethod()]
        public void ApplyOpenVPNIsPure()
        {
            var settings = Settings(SplitTunnelMode.IncludeOnly, "10.0.0.0/8");
            Assert.AreEqual(
                SplitTunnelRewriter.ApplyOpenVPN(FullTunnelOvpn, settings, true),
                SplitTunnelRewriter.ApplyOpenVPN(FullTunnelOvpn, settings, true));
        }

        #endregion

        /// <summary>
        /// Both protocols must describe the same route set for the same settings, so that a
        /// failover from WireGuard to OpenVPN does not silently widen or narrow the tunnel.
        /// </summary>
        [TestMethod()]
        public void BothProtocolsAgreeOnTheRouteSet()
        {
            var settings = Settings(SplitTunnelMode.IncludeOnly, "10.0.0.0/8", "130.89.0.0/16", "2001:db8::/32");

            var wireGuard = SplitTunnelRewriter
                .Apply(FullTunnelAllowedIPs, settings, true)
                .Select(IPPrefixMath.ToCidrString)
                .OrderBy(s => s)
                .ToList();

            var openVpn = SplitTunnelRewriter.Directives(settings, true)
                .Where(l => l.StartsWith("route ") || l.StartsWith("route-ipv6 "))
                .Select(l =>
                {
                    var parts = l.Split(' ');
                    return parts[0] == "route-ipv6"
                        ? parts[1]
                        : parts[1] + "/" + MaskToPrefixLength(parts[2]);
                })
                .OrderBy(s => s)
                .ToList();

            CollectionAssert.AreEqual(wireGuard, openVpn);
        }

        private static int MaskToPrefixLength(string mask)
        {
            return mask.Split('.').Sum(o => CountBits(byte.Parse(o)));
        }

        private static int CountBits(byte value)
        {
            var count = 0;
            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }
            return count;
        }

        private static int Occurrences(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
