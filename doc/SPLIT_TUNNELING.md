# Split tunneling

Split tunneling lets you decide which traffic goes through the VPN. It is a **client-side**
feature: the server keeps handing out its normal configuration, and the client rewrites what it
passes to the tunnel.

It is **off by default** for every server. When it is off, the configuration reaches the tunnel
exactly as the server wrote it, byte for byte.

> **Your institution may not allow this.** Most eduVPN profiles are configured to route *all*
> traffic through the VPN, and overriding that may violate your institution's policy. Traffic you
> exclude is no longer protected by the VPN. The client warns you once per server before you switch
> the feature on.

The feature is part of the shared `eduVPN` project, so it is available in the eduVPN,
Let's Connect! and govVPN clients alike.

## Where to find it

**Settings → Split tunneling.** The group edits one server at a time; pick it from the *Server*
list. Settings are stored per server and survive restarts, session expiry, re-authorization and a
protocol failover from WireGuard to OpenVPN.

Changing settings while you are connected to that server does not take effect immediately; the
group says so, and the new routes become live after reconnecting.

## Modes

| Mode | What goes through the VPN |
| --- | --- |
| **Off** (default) | Whatever the server configured. |
| **Only send the networks below** | Only the networks you list. |
| **Send everything except the networks below** | Everything except the networks you list. |

Networks are entered in CIDR notation, IPv4 or IPv6 — for example `10.0.0.0/8`,
`130.89.0.0/16`, `2001:db8::/32`. A single address such as `145.100.1.1` is accepted and treated
as a `/32` (or `/128`).

A network covering every address (`0.0.0.0/0`, `::/0`) is rejected: it is not a split tunnel, it
either captures everything or disables the tunnel entirely.

### IPv4 and IPv6 are independent

The two address families are handled completely separately. If you only list IPv4 networks, **all
IPv6 traffic keeps using the VPN**, and vice versa. To exclude a dual-stack destination you have to
list both its IPv4 and its IPv6 prefix.

### Profiles that are not full-tunnel

Servers tell the client whether a profile routes everything (`default_gateway`). When a profile
already pushes only a partial route set:

- **Only send the networks below** *narrows* that set rather than widening it. With WireGuard the
  client intersects your list with the pushed `AllowedIPs`, so a network the server does not route
  is not routed. With OpenVPN the pushed routes are dropped and your list is installed instead,
  which the client cannot intersect because it does not know the pushed routes at the time the
  `.ovpn` file is written — a destination the server does not route then becomes unreachable rather
  than leaking.
- **Send everything except the networks below** subtracts your list from the pushed routes.

## DNS

| Setting | WireGuard | OpenVPN |
| --- | --- | --- |
| **Use the DNS servers of the VPN for everything** | Pushed resolvers are kept. | Pushed resolvers are kept. |
| **Use the DNS servers of the VPN for its own domains only** (default when a mode is active) | **Behaves as above** — see below. | Pushed resolvers and `dhcp-option DOMAIN` are kept. |
| **Keep using the DNS servers of this computer** | `DNS`/`DNSSearch` are removed from the tunnel configuration. | `pull-filter ignore "dhcp-option DNS"` is added. |

**WireGuard limitation:** the WireGuard tunnel manager service (`eduWGManager`) applies DNS servers
to the whole tunnel interface. Per-domain resolvers — what the official WireGuard client does with
NRPT rules — are not reachable from the client without changing `eduWGManager`, so for WireGuard
only *…for everything* and *Keep using the DNS servers of this computer* are actually distinct.
`SplitTunnelDnsMode.Split` currently behaves like `Server` for WireGuard; there is a `TODO` at the
hook in `WireGuardSession.Run()`.

## Kill switch × split tunneling

The `WireGuardKillSwitch2` setting (*Block traffic outside VPN*) is applied **first**, then split
tunneling is applied to the resulting `AllowedIPs`.

| Kill switch | Off | Only send the networks below | Send everything except the networks below |
| --- | --- | --- | --- |
| **Preserve** | Server configuration as-is. | Only your networks are tunnelled; the rest uses the normal network. | Everything but your networks is tunnelled. |
| **Enforce** | `0.0.0.0/1 + 128.0.0.0/1` are collapsed into `0.0.0.0/0`, which makes WireGuard install its kill switch. | The kill switch cannot engage: the configuration no longer contains a default route, so traffic outside your list is **not blocked, only not tunnelled**. | The excluded prefixes are the only traffic leaving outside the tunnel. This is intentional and is the closest thing to "kill switch plus split tunnel". |
| **Remove** | `0.0.0.0/0` is split into halves, suppressing the kill switch. | Same as *Preserve*. | Same as *Preserve*. |

So: **Enforce + Only send the networks below** silently weakens the kill switch. The settings page
warns about overriding the profile policy; read the row above before relying on the kill switch
together with an include list.

## What is always protected

Whatever you enter, the following stay intact (`AlwaysIncludeTunnelSubnet`, on by default):

- the **interface address prefixes** of the tunnel stay routed through the tunnel, so the tunnel's
  own subnet remains reachable;
- the **peer endpoint** is carved out of the tunnel, so the tunnel can still reach its own server;
- the **ProxyGuard** endpoint and the keys, endpoint, keep-alive and MTU of the configuration are
  never touched.

## How it is implemented

| Piece | File |
| --- | --- |
| Settings model | `eduVPN/Models/SplitTunnelSettings.cs` |
| Modes | `eduVPN/ViewModels/VPN/SplitTunnelMode.cs` |
| CIDR set arithmetic | `eduVPN/IPPrefixMath.cs` |
| Rewriter | `eduVPN/ViewModels/VPN/SplitTunnelRewriter.cs` |
| Storage | `eduVPN/Xml/SplitTunnelSettingsDictionary.cs`, `Settings.SplitTunnelSettings` |
| WireGuard hook | `eduVPN/ViewModels/VPN/WireGuardSession.cs`, right after the kill-switch block |
| OpenVPN hook | `eduVPN/ViewModels/VPN/OpenVPNSession.cs`, where `Config.VPNConfig` is written |
| UI | `eduVPN/ViewModels/Pages/SplitTunnelSettingsViewModel.cs`, `eduVPN.Views/Pages/SettingsPage.xaml` |

Both rewrite entry points are pure functions of (server configuration, settings,
`default_gateway`) and keep no state, so they are re-applied to every configuration the server
hands out. Nothing is written back into the configuration itself.

### WireGuard

`Peer.AllowedIPs` is recomputed per peer. WireGuard has no exclude syntax, so *Send everything
except…* is implemented as a CIDR complement: the excluded networks are subtracted from the
`AllowedIPs` that came out of the kill-switch step and the minimal covering prefix list is emitted,
per address family.

### OpenVPN

`SplitTunnelRewriter.ApplyOpenVPN()` streams the server configuration and appends its directives,
preserving inline `<ca>`, `<cert>`, `<key>` and `<tls-crypt>` blocks byte for byte using the same
inline-block state machine as the existing `OpenVPNRemoveOptions` filter.

*Only send the networks below* comments out any `redirect-gateway`, `redirect-private`, `route` and
`route-ipv6` option in the client configuration — `--pull-filter` only affects options the server
*pushes* — and appends:

```
pull-filter ignore "redirect-gateway"
pull-filter ignore "redirect-private"
pull-filter ignore "route "
pull-filter ignore "route-ipv6 "
route <net> <mask> vpn_gateway
route-ipv6 <prefix>
```

The trailing space in `"route "` is deliberate: it keeps pushed `route-gateway` and `route-metric`
options. `route-nopull` is deliberately *not* used — from OpenVPN 2.5 on it also drops the pushed
DNS options, which the DNS setting controls separately. The bundled OpenVPN is 2.5, which supports
`--pull-filter` (2.4 and later).

*Send everything except the networks below* leaves the configuration alone and appends:

```
redirect-gateway def1 ipv6
route <net> <mask> net_gateway
route-ipv6 <prefix> net_gateway
```

The directives go **after** the server configuration so that OpenVPN's last-wins rules let them
override anything above, and so that `route` options simply add up. This is the same position the
existing `OpenVPNAddOptions` setting uses.

## Settings schema

A user-scoped setting `SplitTunnelSettings`, keyed by server base URI:

```xml
<SplitTunnelSettingsDictionary>
  <Server Id="https://vpn.example.org/" SchemaVersion="1" Mode="Exclude" Dns="Split"
          AlwaysIncludeTunnelSubnet="True" PolicyWarningAcknowledged="True">
    <Route Prefix="192.168.0.0/16" />
    <Route Prefix="2001:db8::/32" />
  </Server>
</SplitTunnelSettingsDictionary>
```

A missing entry means *Off*. Entries written by a newer schema version than the client understands
are ignored rather than guessed at. Invalid routes are dropped on read and on write; the route list
is capped at 256 entries.

### Pre-seeding through the registry

The setting implements `IRegistrySerializable`, so administrators can pre-seed or enforce it like
the other eduVPN settings. The value is a `REG_MULTI_SZ`, one string per server:

```
<base URI>|<Off|IncludeOnly|Exclude>|<Server|Split|System>|<route>[,<route>…]
```

For example:

```
https://vpn.example.org/|Exclude|Split|192.168.0.0/16,2001:db8::/32
```

A pre-seeded entry counts as the policy decision having been made, so the warning is not shown for
it.

## Known limitations

- **No per-domain DNS for WireGuard.** See the DNS table above.
- **OpenVPN cannot intersect unknown pushed routes.** See *Profiles that are not full-tunnel*.
- **A hostname endpoint is not carved out.** The peer endpoint is only excluded from the tunnel
  when the configuration gives it as a literal address; with a hostname the carve-out is skipped.
  WireGuard and OpenVPN both keep reaching their own server in that case, but a user who excludes
  the endpoint's network explicitly will break the tunnel.
- **No per-application split tunneling.** Windows has route-based split tunneling only; the
  Android client has per-app lists.
- **No per-profile settings.** Settings are per server, not per profile within a server.
- **Enforce + include list weakens the kill switch.** See the matrix above.
