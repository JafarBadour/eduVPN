/*
    eduVPN - VPN for education and research

    Copyright: 2024-2026 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

using eduVPN.Models;
using eduVPN.Properties;
using eduVPN.ViewModels.VPN;
using Microsoft.Win32;
using System;
using System.Linq;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace eduVPN.Xml
{
    /// <summary>
    /// Serializable split tunneling settings, keyed by server base URI
    /// </summary>
    /// <remarks>
    /// <para>A server without an entry has split tunneling switched off, so an empty dictionary is
    /// the same thing as the feature being disabled everywhere.</para>
    /// <para>The dictionary is also readable from the registry, which lets administrators pre-seed
    /// or enforce settings through Group Policy like the other eduVPN settings allow.</para>
    /// </remarks>
    public class SplitTunnelSettingsDictionary : Models.ObservableDictionary<string, SplitTunnelSettings>, IXmlSerializable, IRegistrySerializable
    {
        #region Constants

        private const string EntryElement = "Server";
        private const string RouteElement = "Route";

        #endregion

        #region Methods

        /// <summary>
        /// Returns the settings of a server, or <see cref="SplitTunnelSettings.Off"/> when the
        /// server has none.
        /// </summary>
        /// <param name="serverId">Server base URI</param>
        /// <returns>Settings, brought up to the current schema version</returns>
        public SplitTunnelSettings Get(string serverId)
        {
            if (serverId == null || !TryGetValue(serverId, out var settings) || settings == null)
                return SplitTunnelSettings.Off;
            if (settings.SchemaVersion > SplitTunnelSettings.SchemaVersionCurrent)
                // Written by a newer client: we cannot know what the fields mean, so do nothing.
                return SplitTunnelSettings.Off;
            return settings.Sanitized();
        }

        /// <summary>
        /// Stores the settings of a server, dropping the entry entirely when it would not change
        /// anything.
        /// </summary>
        /// <param name="serverId">Server base URI</param>
        /// <param name="settings">Settings to store</param>
        public void Set(string serverId, SplitTunnelSettings settings)
        {
            if (serverId == null)
                throw new ArgumentNullException(nameof(serverId));
            if (settings == null)
            {
                Remove(serverId);
                return;
            }
            this[serverId] = settings.Sanitized();
        }

        #endregion

        #region IXmlSerializable Support

        /// <summary>
        /// This method is reserved and should not be used.
        /// </summary>
        /// <returns><c>null</c></returns>
        public XmlSchema GetSchema()
        {
            return null;
        }

        /// <summary>
        /// Generates an object from its XML representation.
        /// </summary>
        /// <param name="reader">The <see cref="XmlReader"/> stream from which the object is deserialized.</param>
        public void ReadXml(XmlReader reader)
        {
            if (reader.IsEmptyElement)
                return;

            while (reader.Read() &&
                !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == GetType().Name))
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name != EntryElement)
                    continue;

                var id = reader["Id"];
                if (id == null)
                    continue;
                var settings = new SplitTunnelSettings
                {
                    SchemaVersion = ParseInt(reader["SchemaVersion"], SplitTunnelSettings.SchemaVersionCurrent),
                    Mode = ParseEnum(reader["Mode"], SplitTunnelMode.Off),
                    Dns = ParseEnum(reader["Dns"], SplitTunnelDnsMode.Split),
                    AlwaysIncludeTunnelSubnet = ParseBool(reader["AlwaysIncludeTunnelSubnet"], true),
                    PolicyWarningAcknowledged = ParseBool(reader["PolicyWarningAcknowledged"], false),
                };
                if (!reader.IsEmptyElement)
                {
                    while (reader.Read() &&
                        !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == EntryElement))
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.Name == RouteElement)
                        {
                            var route = reader["Prefix"];
                            if (route != null)
                                settings.Routes.Add(route);
                        }
                    }
                }
                this[id] = settings;
            }
        }

        /// <summary>
        /// Converts an object into its XML representation.
        /// </summary>
        /// <param name="writer">The <see cref="XmlWriter"/> stream to which the object is serialized.</param>
        public void WriteXml(XmlWriter writer)
        {
            foreach (var entry in this)
            {
                var settings = entry.Value ?? SplitTunnelSettings.Off;
                writer.WriteStartElement(EntryElement);
                writer.WriteAttributeString("Id", entry.Key);
                writer.WriteAttributeString("SchemaVersion", settings.SchemaVersion.ToString());
                writer.WriteAttributeString("Mode", settings.Mode.ToString());
                writer.WriteAttributeString("Dns", settings.Dns.ToString());
                writer.WriteAttributeString("AlwaysIncludeTunnelSubnet", settings.AlwaysIncludeTunnelSubnet.ToString());
                writer.WriteAttributeString("PolicyWarningAcknowledged", settings.PolicyWarningAcknowledged.ToString());
                foreach (var route in settings.Routes)
                {
                    writer.WriteStartElement(RouteElement);
                    writer.WriteAttributeString("Prefix", route);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
        }

        #endregion

        #region IRegistrySerializable Support

        /// <summary>
        /// Reads the dictionary from a multi-string registry value.
        /// </summary>
        /// <param name="key">Registry key</param>
        /// <param name="name">Registry value name</param>
        /// <returns><c>true</c> when the value was present and read; <c>false</c> otherwise</returns>
        /// <remarks>
        /// One string per server:
        /// <c>&lt;base URI&gt;|&lt;Off|IncludeOnly|Exclude&gt;|&lt;Server|Split|System&gt;|&lt;route&gt;[,&lt;route&gt;…]</c>
        /// </remarks>
        public bool ReadRegistry(RegistryKey key, string name)
        {
            if (!(key.GetValue(name) is string[] values))
                return false;
            foreach (var value in values)
            {
                var fields = value.Split('|');
                if (fields.Length < 2 || string.IsNullOrWhiteSpace(fields[0]))
                    continue;
                var settings = new SplitTunnelSettings
                {
                    Mode = ParseEnum(fields[1], SplitTunnelMode.Off),
                    Dns = fields.Length >= 3 ? ParseEnum(fields[2], SplitTunnelDnsMode.Split) : SplitTunnelDnsMode.Split,
                    // An administrator who pre-seeds the setting has made the policy decision.
                    PolicyWarningAcknowledged = true,
                };
                if (fields.Length >= 4)
                    settings.Routes.AddRange(
                        fields[3].Split(',').Select(r => r.Trim()).Where(r => r.Length > 0));
                this[fields[0].Trim()] = settings;
            }
            return true;
        }

        #endregion

        #region Implementation

        private static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            return value != null && Enum.TryParse(value, true, out T parsed) ? parsed : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            return value != null && bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static int ParseInt(string value, int fallback)
        {
            return value != null && int.TryParse(value, out var parsed) ? parsed : fallback;
        }

        #endregion
    }
}
