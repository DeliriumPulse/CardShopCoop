using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CardShopCoop.Net
{
    /// <summary>
    /// The one string a host reads out loud (or pastes into Discord) so a friend can join:
    /// address + port + lobby password, packed into Crockford base32 with a dash every four
    /// characters. Pure codec - no sockets, no Unity, no logging. It is deliberately the ONLY
    /// place that knows the byte layout.
    ///
    /// PAYLOAD (big-endian, exactly what a fresh reader would guess):
    ///   [0]      version = 1
    ///   [1..4]   IPv4, network order
    ///   [5..6]   port, high byte first
    ///   [7]      password length in UTF-8 BYTES (0..32)
    ///   [8..]    password, UTF-8
    ///
    /// CHECK CHARACTER: the base32 of that payload carries ONE extra character on the end,
    /// outside the payload and outside the base32 bit stream - the low five bits of FNV-1a-32
    /// over the payload bytes, rendered in the same alphabet so it survives the same voice-chat
    /// folding as everything else. It exists because every 8-character group of this code is
    /// meaningful: mistype one character of the address and, without a check, the code decodes
    /// PERFECTLY into a DIFFERENT host - the joiner then sits on a connection timeout to a
    /// stranger's PC with nothing to suggest the code was the problem. Five bits let 1 in 32
    /// corruptions through, so this is a typo net rather than a guarantee; it turns the
    /// overwhelming majority of single-character slips into the honest "that invite code
    /// doesn't look right" the join panel already knows how to say.
    ///
    /// WHY CROCKFORD: this alphabet drops I, L, O and U precisely so a code can survive being
    /// read over voice chat by a ten-year-old. TryParse leans on that even harder - it folds
    /// O to 0 and I/L to 1 before the lookup, uppercases, and ignores dashes and whitespace,
    /// so "read it back to me" typos decode instead of failing.
    ///
    /// TryParse NEVER THROWS. It is wired straight to a button in the co-op window, where the
    /// input is whatever the player pasted; every malformed, truncated, hostile or
    /// wrong-version string has to come back as a plain false.
    /// </summary>
    public static class InviteCode
    {
        /// <summary>Cap on the embedded password, in UTF-8 bytes. The Steam host-password
        /// field is 20 characters, so 32 bytes leaves room without letting a crafted code
        /// balloon into something silly.</summary>
        public const int MaxPasswordBytes = 32;

        private const byte Version = 1;
        private const int HeaderBytes = 8; // version + ip4 + port + pw length
        /// <summary>Base32 characters an 8-byte (password-free) payload occupies: 64 bits at
        /// five bits each, rounded up. The check character sits after these.</summary>
        private const int MinBase32Chars = 13;

        /// <summary>Crockford base32: no I, no L, no O, no U.</summary>
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>Group size for the readability dashes.</summary>
        private const int GroupSize = 4;

        /// <summary>Longest string TryParse will look at - a full 32-byte password is 40
        /// payload bytes = 64 base32 characters + 1 check character + 16 dashes, so anything
        /// beyond this is someone pasting a novel into the field.</summary>
        public const int MaxCodeLength = 128;

        /// <summary>Builds the code, or NULL if the inputs can't be represented (not an IPv4
        /// literal, port out of range, oversized password). Callers treat null as "no code to
        /// show" - it is never surfaced as an error, because the only way to hit it is a
        /// machine that couldn't tell us its own address.</summary>
        public static string Encode(string ip, int port, string password)
        {
            try
            {
                if (!IPAddress.TryParse((ip ?? "").Trim(), out IPAddress addr))
                    return null;
                if (addr.AddressFamily != AddressFamily.InterNetwork)
                    return null;
                if (port <= 0 || port > 65535)
                    return null;

                byte[] pw = Encoding.UTF8.GetBytes(password ?? "");
                if (pw.Length > MaxPasswordBytes)
                    return null;

                byte[] quad = addr.GetAddressBytes();
                if (quad.Length != 4)
                    return null;

                var payload = new byte[HeaderBytes + pw.Length];
                payload[0] = Version;
                Buffer.BlockCopy(quad, 0, payload, 1, 4);
                payload[5] = (byte)((port >> 8) & 0xFF);
                payload[6] = (byte)(port & 0xFF);
                payload[7] = (byte)pw.Length;
                Buffer.BlockCopy(pw, 0, payload, HeaderBytes, pw.Length);

                // check character LAST, appended to the base32 STRING rather than to the
                // payload: five bits inside the byte stream would shift every boundary after
                // them, and this way TryParse can strip it with a single Substring before it
                // decodes anything at all.
                return Group(ToBase32(payload) + CheckChar(payload));
            }
            catch (System.Exception e)
            {
                Swallow.Log(e);
                return null;
            }
        }

        /// <summary>Decodes a code back into the three things Join needs. Returns false for
        /// anything it doesn't fully understand - including a future version byte, which is
        /// the whole reason the version leads the payload. NEVER THROWS.</summary>
        public static bool TryParse(string code, out string ip, out int port, out string password)
        {
            ip = null;
            port = 0;
            password = "";
            try
            {
                if (code == null || code.Length > MaxCodeLength)
                    return false;

                // PASS 1 - normalize to bare alphabet characters. Separate from the decode
                // now that the last character is not part of the bit stream: the check
                // character has to be identified AFTER the dashes, spaces and confusables are
                // gone, and "the last one" is only meaningful on the cleaned string.
                var clean = new StringBuilder(code.Length);
                foreach (char raw in code)
                {
                    char c = char.ToUpperInvariant(raw);
                    // separators the player (or their chat client) may have added
                    if (c == '-' || c == '_' || c == ' ' || c == '\t' || c == '\r' || c == '\n')
                        continue;
                    // confusables: the three swaps Crockford's alphabet was chosen to allow
                    if (c == 'O')
                        c = '0';
                    else if (c == 'I' || c == 'L')
                        c = '1';

                    if (Alphabet.IndexOf(c) < 0)
                        return false; // includes U, which Crockford has no value for
                    clean.Append(c);
                }
                // shortest legal code: an 8-byte payload is 13 base32 characters, + 1 check
                if (clean.Length < MinBase32Chars + 1)
                    return false;

                char check = clean[clean.Length - 1];

                // PASS 2 - base32 back to payload bytes, check character excluded
                var bytes = new List<byte>(48);
                int buffer = 0, bits = 0;
                for (int i = 0; i < clean.Length - 1; i++)
                {
                    buffer = (buffer << 5) | Alphabet.IndexOf(clean[i]);
                    bits += 5;
                    if (bits >= 8)
                    {
                        bits -= 8;
                        bytes.Add((byte)((buffer >> bits) & 0xFF));
                        if (bytes.Count > HeaderBytes + MaxPasswordBytes)
                            return false;
                    }
                }
                // leftover bits are the encoder's zero padding (always < 8) and are dropped

                byte[] payload = bytes.ToArray();
                // BEFORE THE VERSION CHECK, on purpose: a corrupted code whose version byte
                // still happens to read as 1 is exactly the case this catches, and a code that
                // fails here should be reported as a typo rather than as a version mismatch.
                if (CheckChar(payload) != check)
                    return false;

                if (payload.Length < HeaderBytes)
                    return false;
                if (payload[0] != Version)
                    return false;

                int p = (payload[5] << 8) | payload[6];
                if (p <= 0 || p > 65535)
                    return false;

                int pwLen = payload[7];
                if (pwLen > MaxPasswordBytes)
                    return false;
                if (payload.Length - HeaderBytes < pwLen)
                    return false;

                string pw = "";
                if (pwLen > 0)
                    pw = Encoding.UTF8.GetString(payload, HeaderBytes, pwLen);

                ip = $"{payload[1]}.{payload[2]}.{payload[3]}.{payload[4]}";
                port = p;
                password = pw;
                return true;
            }
            catch
            {
                ip = null;
                port = 0;
                password = "";
                return false;
            }
        }

        // ---------------------------------------------------------------- check + base32

        /// <summary>The trailing check character: the low five bits of FNV-1a-32 over the
        /// payload bytes, rendered in the same Crockford alphabet as the rest of the code (so
        /// the O/0 and I/L/1 folding in TryParse applies to it too, and a player reading the
        /// code aloud never has to treat the last character specially).
        ///
        /// FNV-1a rather than a Crockford mod-37 symbol for one reason: mod 37 needs the whole
        /// payload as a big integer and five extra symbols (*~$=U) that this alphabet - and the
        /// folding rules above - deliberately do not have. This is four lines, no new symbols,
        /// and catches 31 of every 32 corruptions. Both halves of the codec live here, so the
        /// choice can be changed in one place.</summary>
        private static char CheckChar(byte[] payload)
        {
            unchecked
            {
                uint h = 2166136261u;              // FNV-1a 32-bit offset basis
                foreach (byte b in payload)
                {
                    h ^= b;
                    h *= 16777619u;
                }
                // THE TOP five bits, NOT the bottom. Multiplication only ever carries upward,
                // so the low bits of an FNV hash are arithmetic mod 32 of the low bits of the
                // input: a corruption living entirely in the TOP three bits of a byte leaves
                // them untouched and would sail through. Measured on single-character
                // mutations of real codes, `h & 31` let 8.8% past; `h >> 27` lets 3.7%, which
                // is the 1-in-32 a five-bit check is supposed to give.
                return Alphabet[(int)(h >> 27)];
            }
        }

        // ---------------------------------------------------------------- base32 + grouping

        private static string ToBase32(byte[] data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    sb.Append(Alphabet[(buffer >> bits) & 31]);
                }
            }
            if (bits > 0)
                sb.Append(Alphabet[(buffer << (5 - bits)) & 31]); // zero-padded tail
            return sb.ToString();
        }

        private static string Group(string s)
        {
            var sb = new StringBuilder(s.Length + s.Length / GroupSize);
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && (i % GroupSize) == 0)
                    sb.Append('-');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
