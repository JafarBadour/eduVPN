/*
    eduVPN - VPN for education and research

    Copyright: 2024-2026 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

using eduEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace eduVPN
{
    /// <summary>
    /// Set arithmetic on <see cref="IPPrefix"/> lists.
    /// </summary>
    /// <remarks>
    /// <para>IPv4 and IPv6 are always treated as two independent address spaces: subtracting an
    /// IPv4 prefix never influences the IPv6 result and vice versa.</para>
    /// <para>All operations return the minimal list of prefixes covering the result, sorted by
    /// address and then by prefix length.</para>
    /// </remarks>
    public static class IPPrefixMath
    {
        #region Methods

        /// <summary>
        /// Returns the prefix with all host bits cleared, so that two prefixes describing the same
        /// network compare equal.
        /// </summary>
        /// <param name="prefix">IP prefix</param>
        /// <returns>Normalized IP prefix</returns>
        public static IPPrefix Normalize(IPPrefix prefix)
        {
            if (prefix == null)
                throw new ArgumentNullException(nameof(prefix));
            var bytes = prefix.Address.GetAddressBytes();
            for (var bit = (int)prefix.CIDR; bit < bytes.Length * 8; bit++)
                bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
            return new IPPrefix(new IPAddress(bytes), prefix.CIDR);
        }

        /// <summary>
        /// Collapses overlapping and adjacent prefixes into the minimal equivalent list.
        /// </summary>
        /// <param name="prefixes">IP prefixes</param>
        /// <returns>Minimal equivalent list of IP prefixes</returns>
        public static List<IPPrefix> Normalize(IEnumerable<IPPrefix> prefixes)
        {
            return PerFamily(prefixes, (bits, family) => Merge(family));
        }

        /// <summary>
        /// Removes every address in <paramref name="remove"/> from <paramref name="prefixes"/>.
        /// </summary>
        /// <param name="prefixes">IP prefixes to subtract from</param>
        /// <param name="remove">IP prefixes to remove</param>
        /// <returns>Minimal list of prefixes covering exactly <c>prefixes \ remove</c></returns>
        public static List<IPPrefix> Subtract(IEnumerable<IPPrefix> prefixes, IEnumerable<IPPrefix> remove)
        {
            var removeList = remove?.ToList() ?? new List<IPPrefix>();
            return PerFamily(prefixes, (bits, family) =>
                Difference(Merge(family), Merge(removeList.Where(p => BitsOf(p) == bits).ToList())));
        }

        /// <summary>
        /// Intersects two prefix lists.
        /// </summary>
        /// <param name="prefixes">IP prefixes</param>
        /// <param name="other">IP prefixes to intersect with</param>
        /// <returns>Minimal list of prefixes covering exactly <c>prefixes ∩ other</c></returns>
        /// <remarks>An address family missing from either side yields no prefixes for that family,
        /// which is what narrowing a pushed route set must do.</remarks>
        public static List<IPPrefix> Intersect(IEnumerable<IPPrefix> prefixes, IEnumerable<IPPrefix> other)
        {
            var otherList = other?.ToList() ?? new List<IPPrefix>();
            return PerFamily(prefixes, (bits, family) =>
            {
                var right = Merge(otherList.Where(p => BitsOf(p) == bits).ToList());
                var result = new List<AddressRange>();
                foreach (var l in Merge(family))
                    foreach (var r in right)
                    {
                        var start = BigInteger.Max(l.Start, r.Start);
                        var end = BigInteger.Min(l.End, r.End);
                        if (start <= end)
                            result.Add(new AddressRange(start, end));
                    }
                return MergeRanges(result);
            });
        }

        /// <summary>
        /// Determines whether <paramref name="inner"/> is fully contained in <paramref name="outer"/>.
        /// </summary>
        /// <param name="outer">Containing IP prefix</param>
        /// <param name="inner">Contained IP prefix</param>
        /// <returns><c>true</c> when <paramref name="outer"/> covers every address of
        /// <paramref name="inner"/>; <c>false</c> otherwise</returns>
        public static bool Contains(IPPrefix outer, IPPrefix inner)
        {
            if (outer == null || inner == null || BitsOf(outer) != BitsOf(inner))
                return false;
            var o = ToRange(outer);
            var i = ToRange(inner);
            return o.Start <= i.Start && i.End <= o.End;
        }

        /// <summary>
        /// Returns the prefix in unambiguous <c>address/length</c> notation.
        /// </summary>
        /// <param name="prefix">IP prefix</param>
        /// <returns>String representation, always including the prefix length</returns>
        /// <remarks><see cref="IPPrefix.ToString"/> omits the length for host prefixes, which is
        /// fine for a WireGuard configuration but ambiguous when round-tripping stored settings.
        /// </remarks>
        public static string ToCidrString(IPPrefix prefix)
        {
            if (prefix == null)
                throw new ArgumentNullException(nameof(prefix));
            return prefix.Address + "/" + prefix.CIDR;
        }

        /// <summary>
        /// Returns the dotted-quad netmask of an IPv4 prefix, as OpenVPN's <c>route</c> option
        /// expects it.
        /// </summary>
        /// <param name="prefix">IPv4 prefix</param>
        /// <returns>Netmask</returns>
        public static IPAddress Netmask(IPPrefix prefix)
        {
            if (prefix == null)
                throw new ArgumentNullException(nameof(prefix));
            if (prefix.Address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Netmask notation only exists for IPv4", nameof(prefix));
            var bytes = new byte[4];
            for (var bit = 0; bit < prefix.CIDR; bit++)
                bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
            return new IPAddress(bytes);
        }

        /// <summary>
        /// Parses a user supplied network, rejecting anything which is not a usable split tunnel
        /// route.
        /// </summary>
        /// <param name="str">String representation in IP or IP/CIDR form</param>
        /// <param name="prefix">Normalized IP prefix</param>
        /// <returns><c>true</c> when <paramref name="str"/> is a usable route; <c>false</c>
        /// otherwise</returns>
        /// <remarks>A prefix covering the whole address family is rejected: it is not a split
        /// tunnel, it either captures everything or disables the tunnel completely.</remarks>
        public static bool TryParseRoute(string str, out IPPrefix prefix)
        {
            prefix = null;
            if (string.IsNullOrWhiteSpace(str) || str.Length > 64)
                return false;
            var trimmed = str.Trim();
            var slash = trimmed.LastIndexOf('/');
            var address = slash >= 0 ? trimmed.Substring(0, slash) : trimmed;
            if (address.IndexOf('%') >= 0)
                // A zone identifier has no meaning for a route, and IPAddress.Parse() would
                // silently accept it.
                return false;
            if (address.IndexOf(':') < 0 && !IsDottedQuad(address))
                // IPAddress.Parse() accepts the historical shorthand forms - "10.0.0" means
                // 10.0.0.0 and "10" means 0.0.0.10 - which no user means to type and which the
                // Android client rejects. Require all four octets.
                return false;
            IPPrefix parsed;
            try
            {
                // IPPrefix.TryParse() only swallows ArgumentException, while IPAddress.Parse() and
                // byte.Parse() throw FormatException and OverflowException on malformed input.
                if (!IPPrefix.TryParse(str.Trim(), out parsed))
                    return false;
            }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
            catch (ArgumentException) { return false; }
            if (parsed.CIDR == 0)
                return false;
            prefix = Normalize(parsed);
            return true;
        }

        #endregion

        #region Implementation

        /// <summary>
        /// Inclusive range of unsigned addresses within one address family.
        /// </summary>
        private class AddressRange
        {
            public readonly BigInteger Start;
            public readonly BigInteger End;

            public AddressRange(BigInteger start, BigInteger end)
            {
                Start = start;
                End = end;
            }
        }

        /// <summary>
        /// Determines whether the string is a strict dotted-quad IPv4 address.
        /// </summary>
        private static bool IsDottedQuad(string address)
        {
            var octets = address.Split('.');
            if (octets.Length != 4)
                return false;
            foreach (var octet in octets)
            {
                if (octet.Length < 1 || octet.Length > 3)
                    return false;
                foreach (var c in octet)
                    if (c < '0' || c > '9')
                        return false;
                if (int.Parse(octet) > 255)
                    return false;
            }
            return true;
        }

        private static int BitsOf(IPPrefix prefix)
        {
            return prefix.Address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        }

        private static BigInteger ToNumber(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            // BigInteger wants little endian and an explicit zero sign byte.
            var magnitude = new byte[bytes.Length + 1];
            for (var i = 0; i < bytes.Length; i++)
                magnitude[i] = bytes[bytes.Length - 1 - i];
            return new BigInteger(magnitude);
        }

        private static IPAddress ToAddress(BigInteger value, int bits)
        {
            var bytes = new byte[bits / 8];
            var magnitude = value.ToByteArray();
            for (var i = 0; i < bytes.Length && i < magnitude.Length; i++)
                bytes[bytes.Length - 1 - i] = magnitude[i];
            return new IPAddress(bytes);
        }

        private static AddressRange ToRange(IPPrefix prefix)
        {
            var start = ToNumber(Normalize(prefix).Address);
            return new AddressRange(start, start + BigInteger.Pow(2, BitsOf(prefix) - prefix.CIDR) - 1);
        }

        private static List<IPPrefix> PerFamily(IEnumerable<IPPrefix> input, Func<int, List<IPPrefix>, List<AddressRange>> operation)
        {
            var list = input?.Where(p => p != null).ToList() ?? new List<IPPrefix>();
            var result = new List<IPPrefix>();
            foreach (var bits in new[] { 32, 128 })
            {
                var family = list.Where(p => BitsOf(p) == bits).ToList();
                if (family.Count == 0)
                    continue;
                foreach (var range in operation(bits, family))
                    result.AddRange(RangeToPrefixes(range, bits));
            }
            result.Sort(Compare);
            return result;
        }

        private static int Compare(IPPrefix left, IPPrefix right)
        {
            var byFamily = BitsOf(left).CompareTo(BitsOf(right));
            if (byFamily != 0)
                return byFamily;
            var byAddress = ToNumber(left.Address).CompareTo(ToNumber(right.Address));
            return byAddress != 0 ? byAddress : left.CIDR.CompareTo(right.CIDR);
        }

        private static List<AddressRange> Merge(List<IPPrefix> prefixes)
        {
            return MergeRanges(prefixes.Select(ToRange).ToList());
        }

        private static List<AddressRange> MergeRanges(List<AddressRange> ranges)
        {
            var merged = new List<AddressRange>();
            if (ranges.Count == 0)
                return merged;
            var sorted = ranges.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
            var current = sorted[0];
            foreach (var next in sorted.Skip(1))
            {
                if (next.Start <= current.End + 1)
                {
                    if (next.End > current.End)
                        current = new AddressRange(current.Start, next.End);
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }
            merged.Add(current);
            return merged;
        }

        private static List<AddressRange> Difference(List<AddressRange> ranges, List<AddressRange> cuts)
        {
            var remaining = ranges;
            foreach (var cut in cuts)
            {
                var next = new List<AddressRange>();
                foreach (var range in remaining)
                {
                    if (cut.End < range.Start || cut.Start > range.End)
                    {
                        next.Add(range);
                        continue;
                    }
                    if (range.Start < cut.Start)
                        next.Add(new AddressRange(range.Start, cut.Start - 1));
                    if (cut.End < range.End)
                        next.Add(new AddressRange(cut.End + 1, range.End));
                }
                remaining = next;
                if (remaining.Count == 0)
                    break;
            }
            return remaining;
        }

        /// <summary>
        /// Decomposes an inclusive address range into the minimal list of covering prefixes.
        /// </summary>
        private static List<IPPrefix> RangeToPrefixes(AddressRange range, int bits)
        {
            var result = new List<IPPrefix>();
            var start = range.Start;
            while (start <= range.End)
            {
                var count = range.End - start + 1;
                // The largest block is limited by the alignment of the start address...
                var alignment = start.IsZero ? bits : TrailingZeroCount(start);
                // ...and by how many addresses are left.
                var capacity = BitLength(count) - 1;
                var hostBits = Math.Min(Math.Min(alignment, capacity), bits);
                result.Add(new IPPrefix(ToAddress(start, bits), (byte)(bits - hostBits)));
                start += BigInteger.Pow(2, hostBits);
            }
            return result;
        }

        private static int TrailingZeroCount(BigInteger value)
        {
            var count = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                count++;
            }
            return count;
        }

        private static int BitLength(BigInteger value)
        {
            var length = 0;
            while (value > 0)
            {
                value >>= 1;
                length++;
            }
            return length;
        }

        #endregion
    }
}
