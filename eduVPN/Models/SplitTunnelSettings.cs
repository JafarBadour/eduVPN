/*
    eduVPN - VPN for education and research

    Copyright: 2024-2026 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

using eduEx;
using eduVPN.ViewModels.VPN;
using System.Collections.Generic;
using System.Linq;

namespace eduVPN.Models
{
    /// <summary>
    /// Client-side split tunneling settings for a single server
    /// </summary>
    /// <remarks>
    /// <para>These settings are stored per server, keyed by the server base URI, and are applied to
    /// every configuration the server hands out — including configurations fetched again after an
    /// expiry or authorization refresh. They are never written back into the configuration
    /// itself.</para>
    /// <para>The class is deliberately a plain value holder: the rewrite must be a pure function of
    /// (server configuration, settings).</para>
    /// </remarks>
    public class SplitTunnelSettings
    {
        #region Fields

        /// <summary>
        /// Version of the stored representation
        /// </summary>
        /// <remarks>Bump this whenever the meaning of an existing field changes.</remarks>
        public const int SchemaVersionCurrent = 1;

        /// <summary>
        /// Upper bound on the number of routes a user may configure for one server
        /// </summary>
        public const int MaxRoutes = 256;

        /// <summary>
        /// Settings which leave the server configuration alone
        /// </summary>
        public static readonly SplitTunnelSettings Off = new SplitTunnelSettings();

        #endregion

        #region Properties

        /// <summary>
        /// Version of the stored representation
        /// </summary>
        public int SchemaVersion { get; set; } = SchemaVersionCurrent;

        /// <summary>
        /// What to do with the routes the server pushed
        /// </summary>
        public SplitTunnelMode Mode { get; set; } = SplitTunnelMode.Off;

        /// <summary>
        /// IPv4 and IPv6 networks in CIDR notation
        /// </summary>
        public List<string> Routes { get; set; } = new List<string>();

        /// <summary>
        /// What to do with the DNS servers the server pushed
        /// </summary>
        public SplitTunnelDnsMode Dns { get; set; } = SplitTunnelDnsMode.Split;

        /// <summary>
        /// Keep the interface address prefixes routed through the tunnel and keep the peer endpoint
        /// reachable outside of it, whatever the user entered
        /// </summary>
        public bool AlwaysIncludeTunnelSubnet { get; set; } = true;

        /// <summary>
        /// Whether the user confirmed the default-gateway policy warning for this server
        /// </summary>
        public bool PolicyWarningAcknowledged { get; set; }

        /// <summary>
        /// Whether the server configuration must be rewritten before it reaches the tunnel
        /// </summary>
        public bool IsActive => Mode != SplitTunnelMode.Off || Dns == SplitTunnelDnsMode.System;

        #endregion

        #region Methods

        /// <summary>
        /// Returns the configured routes, dropping entries which are not usable networks.
        /// </summary>
        /// <returns>Normalized IP prefixes</returns>
        public List<IPPrefix> ParseRoutes()
        {
            var result = new List<IPPrefix>();
            foreach (var route in Routes.Take(MaxRoutes))
                if (IPPrefixMath.TryParseRoute(route, out var prefix))
                    result.Add(prefix);
            return result;
        }

        /// <summary>
        /// Returns a copy with invalid, duplicate and surplus routes removed.
        /// </summary>
        /// <returns>Sanitized settings</returns>
        public SplitTunnelSettings Sanitized()
        {
            return new SplitTunnelSettings
            {
                SchemaVersion = SchemaVersionCurrent,
                Mode = Mode,
                Routes = ParseRoutes().Select(IPPrefixMath.ToCidrString).Distinct().ToList(),
                Dns = Dns,
                AlwaysIncludeTunnelSubnet = AlwaysIncludeTunnelSubnet,
                PolicyWarningAcknowledged = PolicyWarningAcknowledged,
            };
        }

        /// <summary>
        /// Returns a copy of these settings.
        /// </summary>
        /// <returns>Copy</returns>
        public SplitTunnelSettings Clone()
        {
            return new SplitTunnelSettings
            {
                SchemaVersion = SchemaVersion,
                Mode = Mode,
                Routes = new List<string>(Routes),
                Dns = Dns,
                AlwaysIncludeTunnelSubnet = AlwaysIncludeTunnelSubnet,
                PolicyWarningAcknowledged = PolicyWarningAcknowledged,
            };
        }

        #endregion
    }
}
