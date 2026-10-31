/*
    eduVPN - VPN for education and research

    Copyright: 2024-2026 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

using eduEx;
using eduVPN.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace eduVPN.ViewModels.VPN
{
    /// <summary>
    /// Applies the user's split tunneling settings to a finished server configuration
    /// </summary>
    /// <remarks>
    /// Both entry points are pure functions of (server configuration, settings, default-gateway
    /// flag). The rewriter keeps no state, so it can — and must — be applied again to every
    /// configuration the server hands out, including after an expiry or authorization refresh.
    /// </remarks>
    public static class SplitTunnelRewriter
    {
        #region Constants

        /// <summary>
        /// Options which carry routes in the client configuration itself.
        /// </summary>
        /// <remarks>
        /// <c>--pull-filter</c> only drops options the server <em>pushes</em>. A route written into
        /// the client configuration has to be commented out instead, which is why
        /// <see cref="ApplyOpenVPN"/> streams the configuration rather than only appending to it.
        /// </remarks>
        private static readonly string[] LocalRouteOptions =
        {
            "redirect-gateway",
            "redirect-private",
            "route",
            "route-ipv6",
        };

        private static readonly char[] LineBreaks = { '\r', '\n' };

        #endregion

        #region Methods

        /// <summary>
        /// Rewrites the <c>AllowedIPs</c> of a WireGuard peer.
        /// </summary>
        /// <param name="allowedIPs">Allowed IPs as the server handed them out, after the
        /// kill-switch setting was applied</param>
        /// <param name="settings">The user's settings for this server</param>
        /// <param name="isDefaultGateway">The server's <c>default_gateway</c> flag. When
        /// <c>false</c> the server already pushes a partial route set, so
        /// <see cref="SplitTunnelMode.IncludeOnly"/> intersects with the pushed allowed IPs instead
        /// of replacing them, and <see cref="SplitTunnelMode.Exclude"/> subtracts from them.</param>
        /// <param name="tunnelPrefixes">Interface address prefixes, which stay routed through the
        /// tunnel whatever the user entered</param>
        /// <param name="endpoint">The peer endpoint, which is carved out so that the tunnel can
        /// still reach its own peer; <c>null</c> when the endpoint is a name rather than a
        /// literal</param>
        /// <returns>Rewritten allowed IPs</returns>
        public static List<IPPrefix> Apply(
            IEnumerable<IPPrefix> allowedIPs,
            SplitTunnelSettings settings,
            bool isDefaultGateway,
            IEnumerable<IPPrefix> tunnelPrefixes = null,
            IPPrefix endpoint = null)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            var pushed = allowedIPs?.ToList() ?? new List<IPPrefix>();
            if (settings.Mode == SplitTunnelMode.Off)
                return pushed;

            var configured = settings.ParseRoutes();
            List<IPPrefix> result;
            switch (settings.Mode)
            {
                case SplitTunnelMode.IncludeOnly:
                    result = isDefaultGateway
                        ? IPPrefixMath.Normalize(configured)
                        // The server already narrowed the route set; never widen it.
                        : IPPrefixMath.Intersect(pushed, configured);
                    break;

                case SplitTunnelMode.Exclude:
                    // WireGuard has no exclude syntax, so the complement is computed here. IPv4 and
                    // IPv6 are subtracted independently: excluding only IPv4 ranges leaves ::/0 in
                    // place.
                    result = IPPrefixMath.Subtract(pushed, configured);
                    break;

                default:
                    return pushed;
            }

            if (settings.AlwaysIncludeTunnelSubnet)
            {
                if (tunnelPrefixes != null)
                    result = IPPrefixMath.Normalize(result.Concat(tunnelPrefixes));
                if (endpoint != null)
                    // Routing the peer endpoint into the tunnel would break the tunnel itself.
                    result = IPPrefixMath.Subtract(result, new[] { endpoint });
            }
            return result;
        }

        /// <summary>
        /// Rewrites an OpenVPN configuration.
        /// </summary>
        /// <param name="config">Configuration as handed out by the server</param>
        /// <param name="settings">The user's settings for this server</param>
        /// <param name="isDefaultGateway">The server's <c>default_gateway</c> flag</param>
        /// <returns>The rewritten configuration, or <paramref name="config"/> itself when nothing
        /// needs to change</returns>
        /// <remarks>Inline option blocks such as <c>&lt;ca&gt;</c>, <c>&lt;cert&gt;</c>,
        /// <c>&lt;key&gt;</c> and <c>&lt;tls-crypt&gt;</c> are copied through byte for byte.
        /// </remarks>
        public static string ApplyOpenVPN(string config, SplitTunnelSettings settings, bool isDefaultGateway)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (config == null || !settings.IsActive)
                // Returning the very same string keeps the generated .ovpn byte-identical to what
                // a build without this feature produces.
                return config;

            var removeLocalOptions =
                settings.Mode == SplitTunnelMode.IncludeOnly ? LocalRouteOptions : new string[0];
            var removeLocalDns = settings.Dns == SplitTunnelDnsMode.System;
            // Whatever the server used, so the rewrite does not reflow the file.
            var newLine = config.Contains("\r\n") ? "\r\n" : config.Contains("\n") ? "\n" : Environment.NewLine;

            var output = new StringBuilder();
            string inlineTerm = null;
            var pos = 0;
            while (pos < config.Length)
            {
                // Split off one line, keeping its original terminator.
                var eol = config.IndexOfAny(LineBreaks, pos);
                string line, terminator;
                if (eol < 0)
                {
                    line = config.Substring(pos);
                    terminator = string.Empty;
                    pos = config.Length;
                }
                else
                {
                    line = config.Substring(pos, eol - pos);
                    terminator = config[eol] == '\r' && eol + 1 < config.Length && config[eol + 1] == '\n'
                        ? "\r\n"
                        : config[eol].ToString();
                    pos = eol + terminator.Length;
                }

                var trimmedLine = line.Trim();
                if (!string.IsNullOrEmpty(trimmedLine))
                {
                    if (inlineTerm == null)
                    {
                        // Not inside an inline option block = regular parsing mode.
                        if (!trimmedLine.StartsWith("#") && !trimmedLine.StartsWith(";"))
                        {
                            var option = eduOpenVPN.Configuration.ParseParams(trimmedLine);
                            if (option.Count > 0)
                            {
                                if (option[0].StartsWith("<") && !option[0].StartsWith("</") && option[0].EndsWith(">"))
                                {
                                    // Start of an inline option: copy it through verbatim.
                                    inlineTerm = "</" + option[0].Substring(1, option[0].Length - 2) + ">";
                                }
                                else if (removeLocalOptions.Contains(option[0]) ||
                                    removeLocalDns && option[0] == "dhcp-option" && option.Count > 1 &&
                                        string.Equals(option[1], "DNS", StringComparison.OrdinalIgnoreCase))
                                {
                                    line = "# split-tunnel: " + line;
                                }
                            }
                        }
                    }
                    else if (trimmedLine == inlineTerm)
                    {
                        // Inline option terminator found. Returning to regular parsing mode.
                        inlineTerm = null;
                    }
                }
                output.Append(line).Append(terminator);
            }
            if (output.Length > 0 && output[output.Length - 1] != '\n')
                output.Append(newLine);

            // The directives go last so that OpenVPN's last-wins rules let them override the
            // server configuration above, and so that routes simply add up.
            output.Append(string.Join(newLine, Directives(settings, isDefaultGateway)));
            return output.ToString();
        }

        /// <summary>
        /// Builds the extra OpenVPN directives which implement the user's settings.
        /// </summary>
        /// <param name="settings">The user's settings for this server</param>
        /// <param name="isDefaultGateway">The server's <c>default_gateway</c> flag</param>
        /// <returns>Directives, one per element; empty when nothing needs to change</returns>
        public static List<string> Directives(SplitTunnelSettings settings, bool isDefaultGateway)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            var lines = new List<string>();
            var configured = IPPrefixMath.Normalize(settings.ParseRoutes());
            switch (settings.Mode)
            {
                case SplitTunnelMode.IncludeOnly:
                    // Drop every route the server pushes, then install only what the user listed.
                    // route-nopull is deliberately not used: from OpenVPN 2.5 on it also drops the
                    // pushed DNS options, which the DNS setting controls separately.
                    lines.Add("pull-filter ignore \"redirect-gateway\"");
                    lines.Add("pull-filter ignore \"redirect-private\"");
                    // The trailing space keeps this from matching route-gateway or route-metric.
                    lines.Add("pull-filter ignore \"route \"");
                    lines.Add("pull-filter ignore \"route-ipv6 \"");
                    foreach (var route in configured)
                        lines.Add(RouteDirective(route, true));
                    if (!isDefaultGateway)
                        lines.Add(
                            "# Profile is not default-gateway: the routes above replace the pushed " +
                            "ones, so destinations the server does not route are unreachable.");
                    break;

                case SplitTunnelMode.Exclude:
                    // Keep the full tunnel, but send the excluded networks to the physical gateway.
                    if (isDefaultGateway)
                        lines.Add("redirect-gateway def1 ipv6");
                    foreach (var route in configured)
                        lines.Add(RouteDirective(route, false));
                    break;
            }
            switch (settings.Dns)
            {
                case SplitTunnelDnsMode.System:
                    lines.Add("pull-filter ignore \"dhcp-option DNS\"");
                    break;

                default:
                    // Keep the pushed resolvers.
                    break;
            }
            return lines;
        }

        #endregion

        #region Implementation

        private static string RouteDirective(IPPrefix route, bool viaVpn)
        {
            if (route.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return viaVpn
                    ? "route-ipv6 " + IPPrefixMath.ToCidrString(route)
                    : "route-ipv6 " + IPPrefixMath.ToCidrString(route) + " net_gateway";
            return string.Format(
                "route {0} {1} {2}",
                route.Address,
                IPPrefixMath.Netmask(route),
                viaVpn ? "vpn_gateway" : "net_gateway");
        }

        /// <summary>
        /// Returns the peer endpoint as a host prefix.
        /// </summary>
        /// <param name="host">Endpoint host, which may be a name rather than a literal</param>
        /// <returns>Host prefix, or <c>null</c> when <paramref name="host"/> is not a literal</returns>
        public static IPPrefix EndpointPrefix(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;
            return IPAddress.TryParse(host.Trim(), out var address)
                ? new IPPrefix(address, (byte)(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
                : null;
        }

        #endregion
    }
}
