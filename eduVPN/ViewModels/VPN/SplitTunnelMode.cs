/*
    eduVPN - VPN for education and research

    Copyright: 2024-2026 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

namespace eduVPN.ViewModels.VPN
{
    /// <summary>
    /// Client-side split tunneling mode
    /// </summary>
    public enum SplitTunnelMode
    {
        /// <summary>
        /// Hand the server configuration to the tunnel unchanged. The default.
        /// </summary>
        Off = 0,

        /// <summary>
        /// Only route the configured networks through the tunnel.
        /// </summary>
        IncludeOnly,

        /// <summary>
        /// Route everything except the configured networks through the tunnel.
        /// </summary>
        Exclude,
    }

    /// <summary>
    /// What to do with the DNS servers the server pushed
    /// </summary>
    public enum SplitTunnelDnsMode
    {
        /// <summary>
        /// Resolve every query with the pushed DNS servers.
        /// </summary>
        Server = 0,

        /// <summary>
        /// Resolve only the pushed search domains with the pushed DNS servers.
        /// </summary>
        Split,

        /// <summary>
        /// Drop the pushed DNS servers and keep using the system resolver.
        /// </summary>
        System,
    }
}
