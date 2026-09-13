using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CardShopCoop.Net
{
    /// <summary>
    /// The two things a host needs before an invite code can reach the outside world: a port
    /// opened on the router (UPnP IGD) and the public address to put in the code (the router's
    /// own GetExternalIPAddress, with a STUN binding request as the fallback).
    ///
    /// THREADING: every method here BLOCKS on sockets and HTTP for seconds at a time. Nothing
    /// in this file may be called from Unity's main thread - CoopCore runs the whole sequence
    /// on a worker (the same new-Thread idiom as SendWorldTo) and polls volatile fields for
    /// the result.
    ///
    /// FAILURE POLICY: a home router is not a well-behaved peer. It can answer SSDP and then
    /// serve a 404 for its own description; it can advertise a service and reject every SOAP
    /// call; it can hand back XML with no closing tags. EVERY path here catches, logs at
    /// Info/Warning, and returns null/false. Hosting must be completely unaffected by anything
    /// that happens in this file - the worst outcome allowed is "LAN-only invite code".
    ///
    /// SECURITY: the SSDP LOCATION header is attacker-controllable by anything on the LAN (and
    /// SSDP is unauthenticated by design), so it is treated as untrusted input. We only ever
    /// fetch a description whose host is an IPv4 LITERAL inside a private/link-local range -
    /// see <see cref="IsPrivateHttpUrl"/>. No DNS names (that would allow a rebind), no public
    /// addresses, no https (nothing here should be talking to the internet at large), and the
    /// controlURL resolved out of the description is re-checked against the same rule before a
    /// single SOAP byte is sent. Redirects are refused outright and the URI a reply actually
    /// came from is re-checked before its body is read (see SameTrustedTarget), because a URL
    /// that passed the gate is worth nothing if the fetch can be bounced off it afterwards.
    /// </summary>
    public static class NetHelpers
    {
        private const string Description = "CardShopCoop";
        private const int LeaseSeconds = 86400;      // 24h; re-asked every time hosting starts
        private const int HttpTimeoutMs = 4000;
        private const int MaxHttpBytes = 512 * 1024; // a router that streams forever gets cut off
        private const int SsdpTimeoutMs = 3000;
        private const int StunTimeoutMs = 2000;

        /// <summary>Hostname of the public STUN server, and the address to use when DNS itself
        /// does not answer inside <see cref="StunDnsTimeoutMs"/>. Dns.GetHostAddresses has NO
        /// timeout parameter - on a machine whose resolver is wedged it can block for tens of
        /// seconds, and this runs on the invite worker while the host panel says "resolving".
        /// The literal is Google's long-standing anycast STUN address; it is a FALLBACK only,
        /// never preferred over DNS, and if it ever goes stale the worst case is the same
        /// "couldn't reach the internet resolver" LAN-only code we already handle.</summary>
        private const string StunHost = "stun.l.google.com";
        private const string StunFallbackIp = "74.125.250.129";
        private const int StunDnsTimeoutMs = 2000;

        private static readonly byte[] MagicCookie = { 0x21, 0x12, 0xA4, 0x42 };

        /// <summary>Serializes the two SOAP calls that CHANGE router state. TryMapPort runs on
        /// the invite worker and RemoveMapping on the Shutdown unmap worker (and, since 1.0.38,
        /// on the invite worker itself when a mapping is orphaned by a re-host) - so an add and
        /// a delete for the same port really can be in flight at once, and the loser of that
        /// race is either a mapping nobody removes or a delete that lands before its add.
        /// _mappedPort's claim-first trick makes the delete idempotent; this makes the pair
        /// ordered.</summary>
        private static readonly object MapLock = new object();

        // Discovered gateway. Written by the worker thread, read by the worker and by the
        // Shutdown unmap thread - volatile so the unmap thread can't see a torn/stale view.
        private static volatile string _controlUrl;
        private static volatile string _serviceType;
        private static volatile int _mappedPort; // 0 = we have not opened anything
        /// <summary>Bumped inside MapLock every time a mapping is actually created, so a caller
        /// can name the mapping IT made. The port number cannot do that job: two consecutive
        /// sessions map the identical port, and "delete port 27886" from the older session's
        /// worker would take the newer session's mapping with it.</summary>
        private static long _mapEpoch;
        /// <summary>Negative discovery cache, one resolve run wide (see BeginDiscovery). An
        /// SSDP sweep is a 3-second blocking wait, and a run that fails to find a gateway in
        /// TryMapPort would otherwise pay for the identical sweep again in UpnpExternalIp.</summary>
        private static volatile bool _noGateway;

        /// <summary>True when a mapping is currently ours to remove. CoopCore's Shutdown uses
        /// this as the "only if a mapping was made" gate.</summary>
        public static bool HasMapping => _mappedPort != 0;

        /// <summary>Called once at the START of an invite resolve run, before anything else
        /// here. Clears the "no gateway on this LAN" memo so a player who plugs in a router (or
        /// switches network) between two hosting sessions gets a fresh sweep - while the two
        /// calls INSIDE one run still only sweep once.</summary>
        public static void BeginDiscovery()
        {
            if (_controlUrl == null)
                _noGateway = false;
        }

        // ------------------------------------------------------------------ port mapping

        /// <summary>Asks the router to forward <paramref name="port"/> (TCP) to this PC.
        /// BLOCKING - worker thread only. On false, <paramref name="reason"/> is a short
        /// player-readable phrase; it is never null on the false path.</summary>
        public static bool TryMapPort(int port, out string reason)
        {
            return TryMapPort(port, out reason, out _);
        }

        /// <summary>As above, and hands back the EPOCH of the mapping it created (0 when none
        /// was). Pass that number to <see cref="RemoveMapping(long)"/> to remove that mapping
        /// and nothing else - see the orphan path in CoopCore's invite worker.</summary>
        public static bool TryMapPort(int port, out string reason, out long epoch)
        {
            reason = null;
            epoch = 0;
            lock (MapLock)
            {
                try
                {
                    if (port <= 0 || port > 65535)
                    {
                        reason = "the co-op port is out of range";
                        return false;
                    }
                    if (!EnsureGateway())
                    {
                        reason = "no UPnP router answered";
                        return false;
                    }

                    string local = LocalIPv4ForGateway();
                    if (local == null)
                    {
                        reason = "couldn't work out this PC's LAN address";
                        return false;
                    }

                    // ONE ATTEMPT, LEASED, AND NO PERMANENT RETRY (1.0.38). This used to fall
                    // back to a lease of 0 when the router answered 725
                    // (OnlyPermanentLeasesSupported). That bought a class of hardware at the
                    // price of a hole in the router that NOTHING bounds: if the delete on
                    // Shutdown never lands (the game was killed, the router stopped answering,
                    // the PC moved network), a permanent mapping stays open until a human finds
                    // it in the router's admin page - and a mapping the player does not know
                    // about is exactly the thing the session password exists to defend against.
                    // A router that only takes permanent leases now gets the honest "router
                    // declined" path and the player forwards the port themselves, knowingly.
                    if (AddMapping(port, local, LeaseSeconds))
                    {
                        _mappedPort = port;
                        epoch = ++_mapEpoch;
                        CoopPlugin.Log.LogInfo($"UPnP: asked the router to forward TCP {port} to {local} for {LeaseSeconds}s (this is a REQUEST - double NAT can still block the port)");
                        return true;
                    }

                    reason = "the router refused the port mapping";
                    return false;
                }
                catch (Exception e)
                {
                    reason = e.Message;
                    CoopPlugin.Log.LogWarning("UPnP map failed: " + e.GetType().Name + ": " + e.Message);
                    return false;
                }
            }
        }

        /// <summary>Removes the mapping we made, if any. BLOCKING - worker thread only.
        /// Idempotent and silent about failure. We always ASK for a leased mapping (see
        /// TryMapPort), so a delete that never lands should expire on its own - but a router is
        /// free to clamp or ignore the lease we asked for, so this call, not the lease, is the
        /// thing that actually closes the hole.</summary>
        public static void RemoveMapping()
        {
            RemoveMapping(0L);
        }

        /// <summary>Removes the mapping, but only if it is still the one whose epoch this is.
        /// Zero means "whatever is currently mapped" (Shutdown's case, which owns the live
        /// session by definition). A non-zero epoch that no longer matches means a NEWER
        /// session has already mapped this port since - that mapping belongs to a live host and
        /// must be left exactly where it is.</summary>
        public static void RemoveMapping(long onlyEpoch)
        {
            lock (MapLock)
            {
                if (onlyEpoch != 0L && onlyEpoch != _mapEpoch)
                {
                    CoopPlugin.Log.LogInfo("UPnP: leaving the port mapping alone - a newer session owns it now");
                    return;
                }
                int port = _mappedPort;
                if (port == 0)
                    return;
                _mappedPort = 0; // claim it first: a second caller must not repeat the SOAP call
                try
                {
                    if (_controlUrl == null)
                        return;
                    string inner =
                        "<NewRemoteHost></NewRemoteHost>" +
                        "<NewExternalPort>" + port + "</NewExternalPort>" +
                        "<NewProtocol>TCP</NewProtocol>";
                    if (Soap("DeletePortMapping", inner, out _))
                        CoopPlugin.Log.LogInfo($"UPnP: removed the port {port} mapping");
                    else
                        CoopPlugin.Log.LogWarning($"UPnP: could not remove the port {port} mapping - it should expire with its {LeaseSeconds}s lease, but check your router's port-forwarding page if you want it gone now");
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning("UPnP unmap failed: " + e.Message
                        + " - the mapping should expire with its lease; check your router if you want it gone now");
                }
            }
        }

        private static bool AddMapping(int port, string localIp, int lease)
        {
            string inner =
                "<NewRemoteHost></NewRemoteHost>" +
                "<NewExternalPort>" + port + "</NewExternalPort>" +
                "<NewProtocol>TCP</NewProtocol>" +
                "<NewInternalPort>" + port + "</NewInternalPort>" +
                "<NewInternalClient>" + localIp + "</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>" + Description + "</NewPortMappingDescription>" +
                "<NewLeaseDuration>" + lease + "</NewLeaseDuration>";
            if (!Soap("AddPortMapping", inner, out string body))
                return false;
            // A 200 carrying a fault body is rare but real; treat it as the refusal it is.
            return body == null || body.IndexOf("errorCode", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // ------------------------------------------------------------------ public address

        /// <summary>The router's own idea of its internet-facing address, or null. BLOCKING -
        /// worker thread only. NOTE this can legitimately come back as a PRIVATE address on a
        /// carrier-grade-NAT line; the caller decides what that means, this only reports it.
        /// </summary>
        public static string UpnpExternalIp()
        {
            try
            {
                if (!EnsureGateway())
                    return null;
                if (!Soap("GetExternalIPAddress", "", out string body) || body == null)
                    return null;
                string ip = TagValue(body, "NewExternalIPAddress");
                if (ip == null)
                    return null;
                ip = ip.Trim();
                if (!IPAddress.TryParse(ip, out IPAddress parsed))
                    return null;
                if (parsed.AddressFamily != AddressFamily.InterNetwork)
                    return null;
                CoopPlugin.Log.LogInfo("UPnP: router reports external address " + ip);
                return ip;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogInfo("UPnP external address failed: " + e.Message);
                return null;
            }
        }

        /// <summary>One RFC 5389 binding request to Google's public STUN server, one retry.
        /// Returns the XOR-MAPPED-ADDRESS as a dotted quad, or null. BLOCKING - worker thread
        /// only.</summary>
        public static string StunPublicIp()
        {
            IPEndPoint server = ResolveStun(); // once, not per attempt
            if (server == null)
                return null;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                UdpClient udp = null;
                try
                {
                    // The transaction id is what makes a spoofed reply from anyone on the path
                    // fail ParseStun, so it comes from the crypto RNG rather than from a
                    // TickCount-seeded System.Random a bystander could simply guess.
                    var txn = new byte[12];
                    using (var rng = new RNGCryptoServiceProvider())
                        rng.GetBytes(txn);

                    var req = new byte[20];
                    req[0] = 0x00;
                    req[1] = 0x01;       // Binding Request
                    req[2] = 0x00;
                    req[3] = 0x00;       // no attributes
                    Buffer.BlockCopy(MagicCookie, 0, req, 4, 4);
                    Buffer.BlockCopy(txn, 0, req, 8, 12);

                    udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    udp.Client.ReceiveTimeout = StunTimeoutMs;
                    udp.Send(req, req.Length, server);

                    IPEndPoint from = null;
                    byte[] resp = udp.Receive(ref from);
                    string ip = ParseStun(resp, txn);
                    if (ip != null)
                    {
                        CoopPlugin.Log.LogInfo("STUN: public address " + ip);
                        return ip;
                    }
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogInfo($"STUN attempt {attempt + 1} failed: {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    try
                    {
                        udp?.Close();
                    }
                    catch (System.Exception e) { Swallow.Log(e); }
                }
            }
            return null;
        }

        /// <summary>The STUN server's endpoint: DNS if it answers inside StunDnsTimeoutMs, the
        /// documented literal otherwise. The resolve is pushed onto its own short-lived thread
        /// purely because Dns.GetHostAddresses cannot be given a deadline - abandoning that
        /// thread is safe (it is a background thread that only writes a local) and is the whole
        /// point: the invite worker refuses to wait on it longer than the budget above.</summary>
        private static IPEndPoint ResolveStun()
        {
            IPAddress found = null;
            try
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        foreach (IPAddress a in Dns.GetHostAddresses(StunHost))
                            if (a.AddressFamily == AddressFamily.InterNetwork)
                            {
                                found = a;
                                break;
                            }
                    }
                    catch (System.Exception e) { Swallow.Log(e); /* no DNS, no resolver, no network: the literal below covers it */ }
                })
                {
                    IsBackground = true,
                    Name = "CoopStunDns"
                };
                t.Start();
                if (!t.Join(StunDnsTimeoutMs))
                    CoopPlugin.Log.LogInfo("STUN: DNS did not answer in time - using the fallback address");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogInfo("STUN: DNS lookup could not be started (" + e.Message + ")");
            }

            IPAddress ip = found;
            if (ip == null && !IPAddress.TryParse(StunFallbackIp, out ip))
                return null;
            return new IPEndPoint(ip, 19302);
        }

        /// <summary>XOR-MAPPED-ADDRESS out of a binding success response, or null for anything
        /// that isn't one - including a reply whose transaction id doesn't match ours, which is
        /// how a stray or spoofed datagram gets thrown away.</summary>
        private static string ParseStun(byte[] r, byte[] txn)
        {
            if (r == null || r.Length < 20)
                return null;
            if (r[0] != 0x01 || r[1] != 0x01)
                return null;                 // Binding Success
            for (int i = 0; i < 4; i++)
                if (r[4 + i] != MagicCookie[i])
                    return null;
            for (int i = 0; i < 12; i++)
                if (r[8 + i] != txn[i])
                    return null;

            int end = Math.Min(r.Length, 20 + ((r[2] << 8) | r[3]));
            int pos = 20;
            while (pos + 4 <= end)
            {
                int type = (r[pos] << 8) | r[pos + 1];
                int len = (r[pos + 2] << 8) | r[pos + 3];
                int val = pos + 4;
                if (len < 0 || val + len > end)
                    break;
                // 0x0020 is the RFC attribute; 0x8020 is the pre-RFC comprehension-optional
                // number some servers still answer with.
                if ((type == 0x0020 || type == 0x8020) && len >= 8 && r[val + 1] == 0x01)
                {
                    var q = new byte[4];
                    for (int k = 0; k < 4; k++)
                        q[k] = (byte)(r[val + 4 + k] ^ MagicCookie[k]);
                    return $"{q[0]}.{q[1]}.{q[2]}.{q[3]}";
                }
                pos = val + len;
                pos += (4 - (len & 3)) & 3; // attributes are padded to 4-byte boundaries
            }
            return null;
        }

        // ------------------------------------------------------------------ address helpers

        /// <summary>True for an address that could actually be reached from the internet.
        /// Everything private, loopback, link-local, multicast or carrier-grade-NAT
        /// (100.64/10) is false - a code built from one of those is a code that silently
        /// doesn't work.</summary>
        public static bool IsPublicIPv4(string ip)
        {
            try
            {
                if (!IPAddress.TryParse((ip ?? "").Trim(), out IPAddress a))
                    return false;
                if (a.AddressFamily != AddressFamily.InterNetwork)
                    return false;
                byte[] b = a.GetAddressBytes();
                if (b[0] == 0 || b[0] == 127)
                    return false;                   // this-network, loopback
                if (b[0] == 10)
                    return false;                                  // 10/8
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                    return false;     // 100.64/10 CGNAT
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    return false;      // 172.16/12
                if (b[0] == 192 && b[1] == 168)
                    return false;                   // 192.168/16
                if (b[0] == 169 && b[1] == 254)
                    return false;                   // link-local
                if (b[0] >= 224)
                    return false;                                  // multicast + reserved
                return true;
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        /// <summary>Best guess at this PC's LAN address, ranked the same way the host panel
        /// ranks the addresses it prints (192.168 before 10 before 172, which is usually
        /// WSL/Hyper-V/Docker). Null only when there is no usable adapter at all.</summary>
        public static string LocalIPv4()
        {
            try
            {
                string best = null;
                int bestRank = int.MaxValue;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        string s = addr.Address.ToString();
                        if (s.StartsWith("169.254"))
                            continue; // link-local noise
                        int rank = s.StartsWith("192.168.") ? 0 : s.StartsWith("10.") ? 1 : s.StartsWith("172.") ? 2 : 3;
                        if (rank < bestRank)
                        {
                            bestRank = rank;
                            best = s;
                        }
                    }
                }
                return best;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogInfo("LAN address lookup failed: " + e.Message);
                return null;
            }
        }

        /// <summary>The address the OS would use to TALK to the gateway - the only one the
        /// router will accept as NewInternalClient on a PC with several adapters. A UDP
        /// "connect" picks the route without putting a single byte on the wire.
        ///
        /// NULL WHEN NO GATEWAY HAS BEEN DISCOVERED, and that distinction is the point: the
        /// invite code's LAN fallback address must be THIS one whenever we know it, because
        /// <see cref="LocalIPv4"/> merely RANKS adapters by prefix and a VirtualBox, Hyper-V or
        /// VPN adapter sitting on 192.168.x wins that ranking as easily as the real LAN card
        /// does. Putting the losing adapter in the code while the port mapping points at the
        /// winning one produces a code that cannot possibly work. With no gateway there is
        /// nothing to be route-correct ABOUT, so the caller falls back to the ranking.</summary>
        public static string LocalIPv4ForGateway()
        {
            string url = _controlUrl;
            if (url == null)
                return null;
            try
            {
                var u = new Uri(url);
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPEndPoint(IPAddress.Parse(u.Host), u.Port <= 0 ? 80 : u.Port));
                    return (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? LocalIPv4();
                }
            }
            catch (System.Exception e)
            {
                Swallow.Log(e);
                return LocalIPv4();
            }
        }

        // ------------------------------------------------------------------ SSDP + IGD setup

        /// <summary>Finds (once per process, then caches) the WANIPConnection control URL.
        /// Everything downstream refuses to run without it.</summary>
        private static bool EnsureGateway()
        {
            if (_controlUrl != null)
                return true;
            // One failed sweep per resolve run is enough: without this, a run where TryMapPort
            // found no gateway paid the identical 3-second SSDP wait AGAIN inside
            // UpnpExternalIp, doubling the time the host panel spends saying "resolving".
            if (_noGateway)
                return false;
            _noGateway = true; // every path below that doesn't set _controlUrl is a failure

            string location = SsdpDiscover();
            if (location == null)
                return false;

            // THE SECURITY GATE. Anything on the LAN can answer an M-SEARCH with any LOCATION
            // it likes, so a public (or DNS-named) LOCATION is not a gateway we failed to
            // understand - it is a machine trying to make us fetch a URL of its choosing.
            // SsdpDiscover already applies this rule while picking a reply; it is repeated here
            // because this is the gate that must hold even if that selection ever changes.
            if (!IsPrivateHttpUrl(location))
            {
                CoopPlugin.Log.LogWarning("UPnP: ignoring an SSDP reply whose LOCATION is not a private address - " + location);
                return false;
            }

            string xml = HttpGet(location);
            if (xml == null)
                return false;

            if (!FindWanService(xml, out string controlUrl, out string serviceType))
            {
                CoopPlugin.Log.LogInfo("UPnP: the router's description has no WANIPConnection service - no automatic port forwarding on this router");
                return false;
            }

            string baseUrl = TagValue(xml, "URLBase");
            Uri abs;
            try
            {
                Uri root = (baseUrl != null && IsPrivateHttpUrl(baseUrl.Trim()))
                    ? new Uri(baseUrl.Trim())
                    : new Uri(location);
                abs = new Uri(root, controlUrl);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogInfo("UPnP: unusable controlURL (" + e.Message + ")");
                return false;
            }

            // Same gate again: the controlURL came out of a document we already decided to
            // trust only as far as its host, and it can point anywhere it likes.
            if (!IsPrivateHttpUrl(abs.ToString()))
            {
                CoopPlugin.Log.LogWarning("UPnP: ignoring a controlURL that points off the LAN - " + abs);
                return false;
            }

            _serviceType = serviceType;
            _controlUrl = abs.ToString();
            _noGateway = false; // we have one after all
            CoopPlugin.Log.LogInfo("UPnP: gateway control URL " + _controlUrl + " (" + serviceType + ")");
            return true;
        }

        private static string SsdpDiscover()
        {
            UdpClient udp = null;
            try
            {
                string req =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                    "\r\n";
                byte[] payload = Encoding.ASCII.GetBytes(req);
                var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

                udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                udp.Client.ReceiveTimeout = 500;
                udp.Send(payload, payload.Length, target);
                udp.Send(payload, payload.Length, target); // multicast datagrams get dropped; a second is free

                // KEEP READING UNTIL THE DEADLINE, and take the first LOCATION that PASSES the
                // private-address rule rather than the first one that arrives. M-SEARCH is a
                // multicast question anything on the LAN may answer, and the fastest answer is
                // the one an attacker controls (it does not have to be a real router doing real
                // work first). Returning that reply and only then rejecting it handed a single
                // rogue reply the power to suppress the genuine gateway sitting 20ms behind it.
                bool warned = false;
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(SsdpTimeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    IPEndPoint from = null;
                    byte[] data;
                    try
                    {
                        data = udp.Receive(ref from);
                    }
                    catch (SocketException se)
                    {
                        // A read timeout is the normal way to wait here; anything else means
                        // the socket is unusable and looping on it would spin for 3 seconds.
                        if (se.SocketErrorCode == SocketError.TimedOut)
                            continue;
                        CoopPlugin.Log.LogInfo("UPnP discovery socket error: " + se.SocketErrorCode);
                        break;
                    }
                    string text = Encoding.ASCII.GetString(data);
                    if (text.IndexOf("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    string loc = HeaderValue(text, "LOCATION");
                    if (loc == null)
                        continue;
                    if (IsPrivateHttpUrl(loc))
                        return loc;
                    if (!warned)
                    {
                        warned = true; // once per sweep: a chatty rogue must not flood the log
                        CoopPlugin.Log.LogWarning("UPnP: ignoring an SSDP reply whose LOCATION is not a private address - " + loc);
                    }
                }
                CoopPlugin.Log.LogInfo("UPnP: no InternetGatewayDevice answered the SSDP search");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogInfo("UPnP discovery failed: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                try
                {
                    udp?.Close();
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
            return null;
        }

        /// <summary>Walks the &lt;service&gt; blocks for a WANIPConnection:1 or :2 and pulls
        /// its controlURL. Deliberately string surgery rather than System.Xml: this file must
        /// not add a runtime assembly dependency to a mod whose whole design is "load on every
        /// build", and a router's XML is not trustworthy enough to be worth a parser.</summary>
        private static bool FindWanService(string xml, out string controlUrl, out string serviceType)
        {
            controlUrl = null;
            serviceType = null;
            if (xml == null)
                return false;

            string[] wanted =
            {
                "urn:schemas-upnp-org:service:WANIPConnection:2",
                "urn:schemas-upnp-org:service:WANIPConnection:1"
            };

            foreach (string want in wanted)
            {
                int at = xml.IndexOf(want, StringComparison.OrdinalIgnoreCase);
                while (at >= 0)
                {
                    // The controlURL for THIS service, taken from the WHOLE enclosing <service>
                    // block rather than from the serviceType forward. The conventional element
                    // order puts controlURL after serviceType, but nothing in XML guarantees
                    // it, and reading forward-only from a router that orders them the other way
                    // would silently pick up the NEXT service's control URL - i.e. SOAP calls
                    // aimed at the wrong service.
                    int blockStart = LastIndexOfBefore(xml, "<service>", at);
                    int blockEnd = xml.IndexOf("</service>", at, StringComparison.OrdinalIgnoreCase);
                    if (blockStart < 0)
                        blockStart = at;
                    if (blockEnd < 0)
                        blockEnd = xml.Length;
                    string block = xml.Substring(blockStart, blockEnd - blockStart);
                    string url = TagValue(block, "controlURL");
                    if (url != null && url.Trim().Length > 0)
                    {
                        controlUrl = url.Trim();
                        serviceType = want;
                        return true;
                    }
                    at = xml.IndexOf(want, at + want.Length, StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ HTTP + SOAP

        /// <summary>THE trust boundary for every URL this file touches: an http:// URL whose
        /// host is an IPv4 LITERAL in 10/8, 172.16/12, 192.168/16 or 169.254/16. A hostname is
        /// rejected outright rather than resolved - resolving would hand a LAN attacker a DNS
        /// rebind, and a real IGD always advertises a literal anyway.</summary>
        private static bool IsPrivateHttpUrl(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                    return false;
                var u = new Uri(url);
                if (u.Scheme != Uri.UriSchemeHttp)
                    return false;
                if (!IPAddress.TryParse(u.Host, out IPAddress ip))
                    return false;
                if (ip.AddressFamily != AddressFamily.InterNetwork)
                    return false;
                byte[] b = ip.GetAddressBytes();
                if (b[0] == 10)
                    return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    return true;
                if (b[0] == 192 && b[1] == 168)
                    return true;
                if (b[0] == 169 && b[1] == 254)
                    return true;
                return false;
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        private static string HttpGet(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = HttpTimeoutMs;
                req.ReadWriteTimeout = HttpTimeoutMs;
                req.Proxy = null;             // never send a LAN request through a system proxy
                req.KeepAlive = false;
                req.AllowAutoRedirect = false; // see NoRedirect
                req.UserAgent = Description;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (!SameTrustedTarget(resp))
                        return null;
                    return ReadCapped(resp);
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogInfo("UPnP description fetch failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>One SOAP action against the discovered control URL. Returns false for any
        /// transport error or SOAP fault; <paramref name="body"/> carries whatever text came
        /// back (including the fault) so the caller can decide about a retry.</summary>
        private static bool Soap(string action, string innerXml, out string body)
        {
            body = null;
            string url = _controlUrl, service = _serviceType;
            if (url == null || service == null)
                return false;
            try
            {
                string envelope =
                    "<?xml version=\"1.0\"?>" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                    "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    "<s:Body>" +
                    "<u:" + action + " xmlns:u=\"" + service + "\">" + innerXml + "</u:" + action + ">" +
                    "</s:Body></s:Envelope>";
                byte[] data = Encoding.UTF8.GetBytes(envelope);

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "text/xml; charset=\"utf-8\"";
                req.Headers.Add("SOAPAction", "\"" + service + "#" + action + "\"");
                req.Timeout = HttpTimeoutMs;
                req.ReadWriteTimeout = HttpTimeoutMs;
                req.Proxy = null;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false; // see NoRedirect
                req.UserAgent = Description;
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream())
                    s.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (!SameTrustedTarget(resp))
                        return false;
                    body = ReadCapped(resp);
                    return true;
                }
            }
            catch (WebException we)
            {
                try
                {
                    if (we.Response is HttpWebResponse hr)
                        using (hr)
                            if (SameTrustedTarget(hr))
                                body = ReadCapped(hr);
                }
                catch (System.Exception e) { Swallow.Log(e); }
                CoopPlugin.Log.LogInfo("UPnP " + action + " refused: " + we.Message
                    + (body != null ? " | " + Snip(body) : ""));
                return false;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogInfo("UPnP " + action + " failed: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>NO REDIRECTS, AND THE ANSWER MUST COME FROM WHERE WE ASKED. A real IGD
        /// answers its own description and its own control URL directly - it has no reason to
        /// 301/302 anywhere - while a rogue LAN device that won the SSDP race (or a router
        /// description that points at one) would love to bounce this fetch to a host of its
        /// choosing and have us read the body. AllowAutoRedirect = false stops the client from
        /// following one silently; this re-checks the URI the response actually came from
        /// against the same private-literal rule the request URL had to pass, so a 3xx we did
        /// not follow, or any other way the target could move, ends the exchange instead of
        /// feeding an attacker-chosen body into the XML parsing below.</summary>
        private static bool SameTrustedTarget(HttpWebResponse resp)
        {
            string uri = null;
            try
            {
                uri = resp.ResponseUri != null ? resp.ResponseUri.ToString() : null;
            }
            catch (System.Exception e) { Swallow.Log(e); }
            if (uri != null && IsPrivateHttpUrl(uri))
                return true;
            CoopPlugin.Log.LogWarning("UPnP: refusing a reply that came from somewhere other than the LAN address we asked - " + (uri ?? "(no URI)"));
            return false;
        }

        private static string ReadCapped(HttpWebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            {
                if (s == null)
                    return null;
                var ms = new MemoryStream();
                var buf = new byte[8192];
                int total = 0, n;
                // TOTAL wall clock, not just per-read. ReadWriteTimeout bounds how long ONE
                // read may stall; a responder that dribbles one byte just inside that window,
                // forever, satisfies every per-read timeout while pinning the invite worker (and
                // the Shutdown unmap worker) indefinitely. Two full timeouts is far more than
                // any real router needs to hand over a few KB of XML.
                DateTime start = DateTime.UtcNow;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > MaxHttpBytes)
                        break; // a router that never stops talking gets cut off
                    ms.Write(buf, 0, n);
                    if ((DateTime.UtcNow - start).TotalMilliseconds > 2 * HttpTimeoutMs)
                    {
                        CoopPlugin.Log.LogInfo("UPnP: the reply was still arriving after " + (2 * HttpTimeoutMs) + "ms - using what we have");
                        break;
                    }
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // ------------------------------------------------------------------ tiny text tools

        /// <summary>Last occurrence of <paramref name="needle"/> that starts before
        /// <paramref name="before"/>, or -1. (string.LastIndexOf's length-based overload is a
        /// classic off-by-one trap here; this reads as what it does.)</summary>
        private static int LastIndexOfBefore(string hay, string needle, int before)
        {
            int found = -1, at = 0;
            while (true)
            {
                int i = hay.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase);
                if (i < 0 || i >= before)
                    return found;
                found = i;
                at = i + needle.Length;
            }
        }

        /// <summary>Value of an XML element by LOCAL name, namespace prefix or not.</summary>
        private static string TagValue(string xml, string tag)
        {
            try
            {
                int at = 0;
                while (true)
                {
                    at = xml.IndexOf("<", at, StringComparison.Ordinal);
                    if (at < 0)
                        return null;
                    int gt = xml.IndexOf('>', at);
                    if (gt < 0)
                        return null;
                    string name = xml.Substring(at + 1, gt - at - 1);
                    int colon = name.IndexOf(':');
                    if (colon >= 0)
                        name = name.Substring(colon + 1);
                    if (name.Equals(tag, StringComparison.OrdinalIgnoreCase))
                    {
                        int close = xml.IndexOf("<", gt + 1, StringComparison.Ordinal);
                        if (close < 0)
                            return null;
                        return xml.Substring(gt + 1, close - gt - 1);
                    }
                    at = gt + 1;
                }
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>Value of an HTTP-style header line out of an SSDP response.</summary>
        private static string HeaderValue(string text, string header)
        {
            try
            {
                foreach (string line in text.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length <= header.Length)
                        continue;
                    if (!t.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int colon = t.IndexOf(':');
                    if (colon < 0)
                        continue;
                    string v = t.Substring(colon + 1).Trim();
                    if (v.Length > 0)
                        return v;
                }
            }
            catch (System.Exception e) { Swallow.Log(e); }
            return null;
        }

        private static string Snip(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }
    }
}
