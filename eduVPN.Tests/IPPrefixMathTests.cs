/*
    eduVPN - VPN for education and research

    Copyright: 2024-2026 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

using eduEx;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace eduVPN.Tests
{
    [TestClass()]
    public class IPPrefixMathTests
    {
        private static IPPrefix P(string str)
        {
            Assert.IsTrue(IPPrefixMath.TryParseRoute(str, out var prefix) || IPPrefix.TryParse(str, out prefix), str);
            return prefix;
        }

        private static List<string> Texts(IEnumerable<IPPrefix> prefixes)
        {
            return prefixes.Select(IPPrefixMath.ToCidrString).ToList();
        }

        #region Parsing and normalisation

        [TestMethod()]
        public void NormalizeClearsHostBits()
        {
            Assert.AreEqual("10.0.0.0/8", IPPrefixMath.ToCidrString(IPPrefixMath.Normalize(new IPPrefix("10.1.2.3/8"))));
            Assert.AreEqual("192.168.0.0/16", IPPrefixMath.ToCidrString(IPPrefixMath.Normalize(new IPPrefix("192.168.5.7/16"))));
            Assert.AreEqual("2001:db8::/32", IPPrefixMath.ToCidrString(IPPrefixMath.Normalize(new IPPrefix("2001:db8:1234::1/32"))));
        }

        [TestMethod()]
        public void TryParseRouteAcceptsUsableNetworks()
        {
            Assert.IsTrue(IPPrefixMath.TryParseRoute("10.0.0.0/8", out var v4));
            Assert.AreEqual("10.0.0.0/8", IPPrefixMath.ToCidrString(v4));
            Assert.IsTrue(IPPrefixMath.TryParseRoute("  2001:db8::/32  ", out var v6));
            Assert.AreEqual("2001:db8::/32", IPPrefixMath.ToCidrString(v6));
            Assert.IsTrue(IPPrefixMath.TryParseRoute("145.100.1.1", out var host));
            Assert.AreEqual("145.100.1.1/32", IPPrefixMath.ToCidrString(host));
        }

        [TestMethod()]
        public void TryParseRouteRejectsUnusableInput()
        {
            // A default route is not a split tunnel.
            Assert.IsFalse(IPPrefixMath.TryParseRoute("0.0.0.0/0", out _));
            Assert.IsFalse(IPPrefixMath.TryParseRoute("::/0", out _));
            // Malformed input must be rejected rather than thrown, whatever the user types.
            foreach (var invalid in new[]
            {
                null, "", "   ", "not-an-address", "10.0.0", "10.0.0.0.0", "256.0.0.0",
                "10.0.0.0/33", "10.0.0.0/", "10.0.0.0/-1", "::/129", "fe80::1%eth0",
                "1:2:3:4:5:6:7:8:9", "10.0.0.0/999999999999",
            })
                Assert.IsFalse(IPPrefixMath.TryParseRoute(invalid, out _), "Expected '{0}' to be rejected", invalid);
        }

        [TestMethod()]
        public void NormalizeMergesAdjacentPrefixes()
        {
            CollectionAssert.AreEqual(
                new[] { "10.0.0.0/8" },
                Texts(IPPrefixMath.Normalize(new[] { P("10.0.0.0/9"), P("10.128.0.0/9") })));
        }

        [TestMethod()]
        public void NormalizeDropsNestedPrefixes()
        {
            CollectionAssert.AreEqual(
                new[] { "10.0.0.0/8" },
                Texts(IPPrefixMath.Normalize(new[] { P("10.0.0.0/8"), P("10.0.0.0/8"), P("10.1.0.0/16") })));
        }

        [TestMethod()]
        public void NetmaskNotation()
        {
            Assert.AreEqual("255.0.0.0", IPPrefixMath.Netmask(P("10.0.0.0/8")).ToString());
            Assert.AreEqual("255.255.0.0", IPPrefixMath.Netmask(P("130.89.0.0/16")).ToString());
            Assert.AreEqual("255.255.255.255", IPPrefixMath.Netmask(P("1.2.3.4/32")).ToString());
            Assert.AreEqual("0.0.0.0", IPPrefixMath.Netmask(new IPPrefix("0.0.0.0/0")).ToString());
        }

        #endregion

        #region Subtraction

        [TestMethod()]
        public void SubtractPrivateRangeFromDefaultRoute()
        {
            var result = IPPrefixMath.Subtract(new[] { new IPPrefix("0.0.0.0/0") }, new[] { P("10.0.0.0/8") });
            CollectionAssert.AreEqual(
                new[]
                {
                    "0.0.0.0/5", "8.0.0.0/7", "11.0.0.0/8", "12.0.0.0/6",
                    "16.0.0.0/4", "32.0.0.0/3", "64.0.0.0/2", "128.0.0.0/1",
                },
                Texts(result));
            AssertCoversExactly(new[] { new IPPrefix("0.0.0.0/0") }, new[] { P("10.0.0.0/8") }, result);
        }

        [TestMethod()]
        public void SubtractLanFromDefaultRoute()
        {
            var basePrefixes = new[] { new IPPrefix("0.0.0.0/0") };
            var remove = new[] { P("192.168.0.0/16") };
            var result = IPPrefixMath.Subtract(basePrefixes, remove);
            AssertCoversExactly(basePrefixes, remove, result);
            Assert.IsFalse(Texts(result).Contains("192.168.0.0/16"));
            Assert.AreEqual("0.0.0.0/1", Texts(result).First());
        }

        [TestMethod()]
        public void SubtractKeepsAddressFamiliesIndependent()
        {
            var result = IPPrefixMath.Subtract(
                new[] { new IPPrefix("0.0.0.0/0"), new IPPrefix("::/0") },
                new[] { P("192.168.0.0/16") });
            // Only IPv4 was excluded, so the IPv6 default route survives untouched.
            Assert.IsTrue(Texts(result).Contains("::/0"));
            Assert.IsFalse(Texts(result).Contains("0.0.0.0/0"));
        }

        [TestMethod()]
        public void SubtractIPv6Range()
        {
            var basePrefixes = new[] { new IPPrefix("::/0") };
            var remove = new[] { P("2001:db8::/32") };
            var result = IPPrefixMath.Subtract(basePrefixes, remove);
            AssertCoversExactly(basePrefixes, remove, result);
            Assert.IsFalse(Texts(result).Contains("2001:db8::/32"));
        }

        [TestMethod()]
        public void SubtractHandlesOverlappingAndNestedExclusions()
        {
            var basePrefixes = new[] { P("10.0.0.0/8") };
            var remove = new[]
            {
                P("10.1.0.0/16"),
                P("10.1.2.0/24"), // nested in the previous one
                P("10.2.0.0/15"), // overlaps 10.3.0.0/16 as well
                P("10.3.0.0/16"),
            };
            var result = IPPrefixMath.Subtract(basePrefixes, remove);
            CollectionAssert.AreEqual(
                new[]
                {
                    "10.0.0.0/16", "10.4.0.0/14", "10.8.0.0/13", "10.16.0.0/12",
                    "10.32.0.0/11", "10.64.0.0/10", "10.128.0.0/9",
                },
                Texts(result));
            AssertCoversExactly(basePrefixes, remove, result);
        }

        [TestMethod()]
        public void SubtractEverythingYieldsNothing()
        {
            var result = IPPrefixMath.Subtract(
                new[] { P("10.0.0.0/8") },
                new[] { P("10.0.0.0/9"), P("10.128.0.0/9") });
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod()]
        public void SubtractUnrelatedRangeChangesNothing()
        {
            CollectionAssert.AreEqual(
                new[] { "10.0.0.0/8" },
                Texts(IPPrefixMath.Subtract(new[] { P("10.0.0.0/8") }, new[] { P("192.168.0.0/16") })));
        }

        [TestMethod()]
        public void SubtractCarvesOutASingleHost()
        {
            var basePrefixes = new[] { new IPPrefix("0.0.0.0/0") };
            var remove = new[] { P("145.100.1.1/32") };
            var result = IPPrefixMath.Subtract(basePrefixes, remove);
            AssertCoversExactly(basePrefixes, remove, result);
            // 2^32 - 1 addresses need exactly 32 prefixes.
            Assert.AreEqual(32, result.Count);
        }

        #endregion

        #region Intersection

        [TestMethod()]
        public void IntersectNarrowsPushedRoutes()
        {
            var result = IPPrefixMath.Intersect(
                new[] { P("130.89.0.0/16"), P("145.100.0.0/16") },
                new[] { P("130.89.0.0/24"), P("10.0.0.0/8") });
            // 10.0.0.0/8 is not pushed, so it is not routed; the /24 narrows the pushed /16.
            CollectionAssert.AreEqual(new[] { "130.89.0.0/24" }, Texts(result));
        }

        [TestMethod()]
        public void IntersectOfDisjointFamiliesIsEmpty()
        {
            Assert.AreEqual(
                0,
                IPPrefixMath.Intersect(new[] { P("10.0.0.0/8") }, new[] { P("2001:db8::/32") }).Count);
        }

        [TestMethod()]
        public void ContainsAndFamilies()
        {
            Assert.IsTrue(IPPrefixMath.Contains(P("10.0.0.0/8"), P("10.1.0.0/16")));
            Assert.IsFalse(IPPrefixMath.Contains(P("10.1.0.0/16"), P("10.0.0.0/8")));
            Assert.IsFalse(IPPrefixMath.Contains(new IPPrefix("0.0.0.0/0"), new IPPrefix("::/0")));
            Assert.IsFalse(IPPrefixMath.Contains(null, P("10.0.0.0/8")));
        }

        #endregion

        /// <summary>
        /// Reference check: compares the result against the address set computed independently with
        /// plain interval arithmetic, so a wrong prefix decomposition cannot slip through.
        /// </summary>
        private static void AssertCoversExactly(
            IEnumerable<IPPrefix> basePrefixes,
            IEnumerable<IPPrefix> removed,
            IEnumerable<IPPrefix> result)
        {
            var baseList = basePrefixes.ToList();
            var removedList = removed.ToList();
            var resultList = result.ToList();
            foreach (var isIPv6 in new[] { false, true })
            {
                var expected = SubtractIntervals(
                    Intervals(baseList.Where(p => IsIPv6(p) == isIPv6)),
                    Intervals(removedList.Where(p => IsIPv6(p) == isIPv6)));
                var actual = Intervals(resultList.Where(p => IsIPv6(p) == isIPv6));
                CollectionAssert.AreEqual(
                    expected, actual,
                    "Address set differs for {0}", isIPv6 ? "IPv6" : "IPv4");
            }
        }

        private static bool IsIPv6(IPPrefix prefix)
        {
            return prefix.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        private static List<string> Intervals(IEnumerable<IPPrefix> prefixes)
        {
            var sorted = prefixes
                .Select(p =>
                {
                    var start = ToNumber(p);
                    return new KeyValuePair<BigInteger, BigInteger>(
                        start,
                        start + BigInteger.Pow(2, (IsIPv6(p) ? 128 : 32) - p.CIDR) - 1);
                })
                .OrderBy(kv => kv.Key)
                .ToList();
            var merged = new List<KeyValuePair<BigInteger, BigInteger>>();
            foreach (var interval in sorted)
            {
                if (merged.Count > 0 && interval.Key <= merged[merged.Count - 1].Value + 1)
                    merged[merged.Count - 1] = new KeyValuePair<BigInteger, BigInteger>(
                        merged[merged.Count - 1].Key,
                        BigInteger.Max(merged[merged.Count - 1].Value, interval.Value));
                else
                    merged.Add(interval);
            }
            return merged.Select(kv => kv.Key + "-" + kv.Value).ToList();
        }

        private static List<string> SubtractIntervals(List<string> basePrefixes, List<string> cuts)
        {
            var remaining = basePrefixes.Select(Parse).ToList();
            foreach (var cut in cuts.Select(Parse))
            {
                var next = new List<KeyValuePair<BigInteger, BigInteger>>();
                foreach (var interval in remaining)
                {
                    if (cut.Value < interval.Key || cut.Key > interval.Value)
                    {
                        next.Add(interval);
                        continue;
                    }
                    if (interval.Key < cut.Key)
                        next.Add(new KeyValuePair<BigInteger, BigInteger>(interval.Key, cut.Key - 1));
                    if (cut.Value < interval.Value)
                        next.Add(new KeyValuePair<BigInteger, BigInteger>(cut.Value + 1, interval.Value));
                }
                remaining = next;
            }
            return remaining.Select(kv => kv.Key + "-" + kv.Value).ToList();
        }

        private static KeyValuePair<BigInteger, BigInteger> Parse(string interval)
        {
            var parts = interval.Split('-');
            return new KeyValuePair<BigInteger, BigInteger>(
                BigInteger.Parse(parts[0]), BigInteger.Parse(parts[1]));
        }

        private static BigInteger ToNumber(IPPrefix prefix)
        {
            var bytes = IPPrefixMath.Normalize(prefix).Address.GetAddressBytes();
            var magnitude = new byte[bytes.Length + 1];
            for (var i = 0; i < bytes.Length; i++)
                magnitude[i] = bytes[bytes.Length - 1 - i];
            return new BigInteger(magnitude);
        }
    }
}
