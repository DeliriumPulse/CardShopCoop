using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Reflection bridge to the <b>Grading Overhaul</b> mod (munch.gradingoverhaul), which
    /// both players run. With it installed, a graded card's <c>CardData.cardGrade</c> is NOT
    /// 1-10 - it's an ENCODED int packing (grading company + real 1-10 grade + cert serial),
    /// e.g. 380009117. Grading Overhaul exposes a public integration API for exactly this
    /// (co-op) case:
    ///  - <c>EncodedGradeRegistry.RememberForExternalMod(CardData, int)</c> burns + binds the
    ///    cert and registers the encoded grade, so its anti-cheat AddCard prefix leaves a
    ///    received graded card alone instead of re-encoding it as FAKE (the changing-grade
    ///    churn we used to drop as "corrupt").
    ///  - <c>EncodedGradeRegistry.GetEncodedOrCurrent(CardData)</c> returns the true encoded
    ///    grade even when a display transiently swapped in the bare 1-10.
    ///  - <c>Helper.GetActualGrade(int)</c> decodes an encoded grade to its 1-10 value.
    /// All accessed by reflection (Grading Overhaul is not a compile-time dependency). When
    /// it's absent, <see cref="Present"/> is false and callers keep vanilla 1-10 behavior.
    /// Namespace is TCGCardShopSimulator.GradingOverhaul (verified in the decompiled 3.4.2).
    /// </summary>
    public static class GradingInterop
    {
        /// <summary>SIMPLE ASSEMBLY NAME of Grading Overhaul, which is NOT its namespace and
        /// not a compressed form of it: the plugin ships as "Grading Overhaul.dll" and declares
        /// [assembly: AssemblyTitle("Grading Overhaul")] - space and all - while its types live
        /// under TCGCardShopSimulator.GradingOverhaul (verified in the decompiled 3.4.2). It is
        /// only the fast path; ResolveType falls back to the type walk if this is ever wrong,
        /// so a rename by GO costs a little log noise on Game Pass, never the integration.
        /// PUBLIC because Sync/GradingSync resolves one of GO's own patch classes with it (the
        /// day-start block); there must be exactly one spelling of this string in the mod.</summary>
        public const string GradingAssembly = "Grading Overhaul";

        // Assembly-qualified bind first, app-domain type walk only if it misses. This is a
        // STATIC INITIALIZER: it runs the first time anything touches GradingInterop, and with
        // the bare walk that was one guaranteed ReflectionTypeLoadException in the log of every
        // Game Pass session that ever synced a card. See Util.ModParity.ResolveType.
        private static readonly Type TReg = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.EncodedGradeRegistry", GradingAssembly);
        private static readonly Type THelper = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.Helper", GradingAssembly);

        // void RememberForExternalMod(CardData, int) - public static, decompiled :5900
        private static readonly MethodInfo MiRemember = TReg == null ? null
            : ReflectionSurface.OptionalMethod(TReg, "RememberForExternalMod", new[] { typeof(CardData), typeof(int) });
        // int GetEncodedOrCurrent(CardData) - public static, decompiled :5929
        private static readonly MethodInfo MiGetEncoded = TReg == null ? null
            : ReflectionSurface.OptionalMethod(TReg, "GetEncodedOrCurrent", new[] { typeof(CardData) });
        // int GetActualGrade(int) - public static, decompiled :15993
        private static readonly MethodInfo MiActual = THelper == null ? null
            : ReflectionSurface.OptionalMethod(THelper, "GetActualGrade", new[] { typeof(int) });

        // ------------------------------------------------------------------
        // JOB ENROLLMENT bridge (GradingSync.HostApplyOp).
        //
        // A submission is only a Grading Overhaul job because GO's OnPressSubmitButton
        // POSTFIX stamps it: GradingJobSubmissionRegistryPatch (decompiled :12866-12895)
        // registers the company, takes a jobId, ENCODES that into m_ServiceLevel and
        // pre-rolls the grades. That postfix is gated on
        // m_GradeCardInProgressList.Contains(m_CurrentGradeCardSubmitSet), so it can
        // never fire for a set the HOST enrolled on a guest's behalf off the wire -
        // GO's maturation then fails TryDecode, misses the reference-keyed registry and
        // the cards come back vanilla-graded. These members let GradingSync replay that
        // exact chain by reflection for a wire-born set.
        // ------------------------------------------------------------------

        private static readonly Type TTheme = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.GradingWebsiteThemeController", GradingAssembly);
        /// <summary>GO's GradingCompany enum (decompiled :1036 - Cardinals, Custom, PSA, Beckett).
        /// Resolved, never hard-coded: a future GO reorder must show up as a decode mismatch,
        /// not as silently mis-stamped jobs. <see cref="LogCompanyEnum"/> prints it once.</summary>
        private static readonly Type TCompany = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.GradingCompany", GradingAssembly);
        private static readonly Type TJobRegistry = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.GradingJobCompanyRegistry", GradingAssembly);
        private static readonly Type TPreRoll = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.PreRollManager", GradingAssembly);
        private static readonly Type TCodec = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.ServiceLevelCodec", GradingAssembly);
        private static readonly Type TConfig = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.ConfigSettings", GradingAssembly);

        // GradingCompany CurrentWebsiteCompany { get; private set; } - decompiled :4108.
        // Read RAW. GO's own recording postfixes read exactly this property with no fallback
        // (:7940, :12884); only the VALIDATION prefix (:13064) substitutes
        // ConfigSettings.ActiveCompanyProfile when the raw value is Cardinals, and copying that
        // fallback here would stamp jobs with a company the recorder never saw.
        private static readonly PropertyInfo PiCurrentCompany = TTheme == null ? null
            : ReflectionSurface.OptionalProperty(TTheme, "CurrentWebsiteCompany");
        // private static readonly GradingCompany[] AllowedCompanies - decompiled :4017.
        // The website can only ever select these three; Custom (=1) is the internal cheat skin.
        private static readonly FieldInfo FiAllowed = TTheme == null ? null
            : ReflectionSurface.OptionalField(TTheme, "AllowedCompanies");
        // public static ConfigEntry<bool> UseCheatsWebsite - decompiled :9585.
        private static readonly FieldInfo FiUseCheats = TConfig == null ? null
            : ReflectionSurface.OptionalField(TConfig, "UseCheatsWebsite");

        private static readonly MethodInfo MiOnJobSubmitted = (TJobRegistry == null || TCompany == null) ? null
            : ReflectionSurface.OptionalMethod(TJobRegistry, "OnJobSubmitted",
                new[] { typeof(GradeCardSubmitSet), TCompany, typeof(bool), TCompany });
        private static readonly MethodInfo MiNextJobId = TPreRoll == null ? null
            : ReflectionSurface.OptionalMethod(TPreRoll, "GetNextJobId", Type.EmptyTypes);
        private static readonly MethodInfo MiPreRoll = (TPreRoll == null || TCompany == null) ? null
            : ReflectionSurface.OptionalMethod(TPreRoll, "PreRollOnSubmit",
                new[] { typeof(GradeCardSubmitSet), TCompany, typeof(bool), typeof(int) });
        // int Encode(GradingCompany, int tierIndex, int jobId) - decompiled :5574. The class is
        // INTERNAL, so this is unreachable without reflection even though the method is public.
        // Overloaded (a 2-arg Encode exists at :5553), hence the explicit signature.
        private static readonly MethodInfo MiEncode = (TCodec == null || TCompany == null) ? null
            : ReflectionSurface.OptionalMethod(TCodec, "Encode", new[] { TCompany, typeof(int), typeof(int) });
        // bool TryDecode(int, out GradingCompany, out int) - decompiled :5604 (also overloaded).
        private static readonly MethodInfo MiTryDecode = (TCodec == null || TCompany == null) ? null
            : ReflectionSurface.OptionalMethod(TCodec, "TryDecode",
                new[] { typeof(int), TCompany.MakeByRefType(), typeof(int).MakeByRefType() });

        /// <summary>Sentinel written on the wire when GO is absent or the company is unreadable.
        /// Chosen outside any plausible enum ordinal so the host can reject it by the same
        /// <see cref="IsAllowedCompany"/> test it applies to everything else.</summary>
        public const int NoCompany = 255;

        private static bool _logged;

        /// <summary>True when Grading Overhaul is loaded and the integration API resolved.</summary>
        public static bool Present
        {
            get
            {
                if (MiRemember != null && !_logged)
                {
                    _logged = true;
                    CoopPlugin.Log.LogInfo("Grading Overhaul detected - graded cards will sync via its encoded-grade API");
                    LogCompanyEnum();
                }
                return MiRemember != null;
            }
        }

        /// <summary>One-shot startup dump of GO's GradingCompany members and ordinals. The wire
        /// carries a company as its ORDINAL, and both peers' hosts encode/decode with it, so a
        /// future GO release that inserts or reorders a member silently re-points every stamped
        /// job. Printing the resolved table means the first field log after such a release shows
        /// the change instead of hiding it behind mis-graded cards.</summary>
        private static void LogCompanyEnum()
        {
            try
            {
                if (TCompany == null || !TCompany.IsEnum)
                {
                    CoopPlugin.Log.LogWarning("GradingInterop: GO present but GradingCompany enum did not resolve - job enrollment disabled");
                    return;
                }
                var names = Enum.GetNames(TCompany);
                var parts = new string[names.Length];
                for (int i = 0; i < names.Length; i++)
                    parts[i] = names[i] + "=" + Convert.ToInt32(Enum.Parse(TCompany, names[i]));
                CoopPlugin.Log.LogInfo("GradingInterop: GO GradingCompany = " + string.Join(", ", parts)
                    + "; allowed on the website = " + AllowedList());
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.LogCompanyEnum: " + e.Message); }
        }

        private static string AllowedList()
        {
            try
            {
                var arr = FiAllowed?.GetValue(null) as Array;
                if (arr == null)
                    return "<unreadable>";
                var parts = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    object v = arr.GetValue(i);
                    parts[i] = v + "=" + Convert.ToInt32(v);
                }
                return string.Join(", ", parts);
            }
            catch (System.Exception e) { Swallow.Log(e); return "<unreadable>"; }
        }

        /// <summary>The grading company the local website is showing RIGHT NOW, as an ordinal,
        /// or <see cref="NoCompany"/> when GO is absent or the property will not read. Read raw
        /// (see <see cref="PiCurrentCompany"/>) because this is what GO's recording postfixes
        /// stamp a job with - the wire has to carry the same value the guest's own postfix
        /// would have recorded had it been allowed to run.</summary>
        public static int CurrentCompanyId
        {
            get
            {
                if (PiCurrentCompany == null)
                    return NoCompany;
                try
                {
                    object v = PiCurrentCompany.GetValue(null, null);
                    return v == null ? NoCompany : Convert.ToInt32(v);
                }
                catch (System.Exception e) { Swallow.Log(e); return NoCompany; }
            }
        }

        /// <summary>Host-side gate on a wire-supplied company ordinal: it must be a real
        /// GradingCompany member AND one the website is actually allowed to select. The second
        /// half is the load-bearing one - Custom (ordinal 1) is a defined member but it is GO's
        /// internal cheat skin, never offered by the site, and a crafted wire must not be able to
        /// enrol a job under it.</summary>
        public static bool IsAllowedCompany(int id)
        {
            if (TCompany == null || !TCompany.IsEnum)
                return false;
            try
            {
                if (!Enum.IsDefined(TCompany, Enum.ToObject(TCompany, id)))
                    return false;
                var arr = FiAllowed?.GetValue(null) as Array;
                if (arr == null)
                {
                    // Field walked away (GO rename/refactor). Keep the one rule that actually
                    // protects the shared album rather than opening the gate: everything except
                    // the cheat skin. Name-based so a reorder cannot turn this into "allow all".
                    string n = Enum.GetName(TCompany, Enum.ToObject(TCompany, id));
                    CoopPlugin.Log.LogWarning("GradingInterop: GO AllowedCompanies unreadable - falling back to name check for " + n);
                    return n != null && n != "Custom";
                }
                for (int i = 0; i < arr.Length; i++)
                    if (Convert.ToInt32(arr.GetValue(i)) == id)
                        return true;
                return false;
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        /// <summary>Display name of a company ordinal, for the enrollment log line.</summary>
        public static string CompanyName(int id)
        {
            if (TCompany == null || !TCompany.IsEnum)
                return id.ToString();
            try
            {
                return Enum.GetName(TCompany, Enum.ToObject(TCompany, id)) ?? id.ToString();
            }
            catch (System.Exception e) { Swallow.Log(e); return id.ToString(); }
        }

        /// <summary>This machine's Grading Overhaul "Enable Probability System (FAKE CARDS)"
        /// setting (ConfigSettings.UseCheatsWebsite, decompiled :9585/:9739). Defaults to false
        /// on any failure to read.
        ///
        /// The HOST's value is deliberately the one GradingSync uses for a guest's submission,
        /// even though GO's postfix would have read the SUBMITTER's. Grading results land in the
        /// shared album that every peer mirrors, and useCheats=true makes GO grade the job with
        /// the Custom slider odds and stamp the cards as FAKE. Taking the flag off the wire would
        /// let a guest push fake cards into the host's save with the host's own config saying no.
        /// The cost of host-authority is the reverse case: a guest with cheats on submitting to a
        /// clean host gets honest grades - which is the failure direction to prefer.</summary>
        public static bool HostUseCheatsWebsite
        {
            get
            {
                try
                {
                    var entry = FiUseCheats?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                    return entry != null && entry.Value;
                }
                catch (System.Exception e) { Swallow.Log(e); return false; }
            }
        }

        /// <summary>True when every reflection member the enrollment chain needs resolved.
        /// Checked before the chain runs so a partial resolve leaves the raw tier alone
        /// instead of half-stamping a job.</summary>
        public static bool CanEnroll
        {
            get
            {
                return TCompany != null && TCompany.IsEnum
                    && MiOnJobSubmitted != null && MiNextJobId != null
                    && MiPreRoll != null && MiTryDecode != null;
            }
        }

        /// <summary>Step (b) of GO's chain: GradingJobCompanyRegistry.OnJobSubmitted (decompiled
        /// :10209). embeddedCompany is passed as the same company, exactly as GO does at :12886.</summary>
        public static bool RegisterJobCompany(GradeCardSubmitSet set, int companyId, bool useCheats)
        {
            if (set == null || MiOnJobSubmitted == null)
                return false;
            try
            {
                object company = Enum.ToObject(TCompany, companyId);
                MiOnJobSubmitted.Invoke(null, new object[] { set, company, useCheats, company });
                return true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.RegisterJobCompany: " + e.Message); return false; }
        }

        /// <summary>Step (c): PreRollManager.GetNextJobId (decompiled :5366). 0 on failure - GO
        /// treats jobId &lt;= 0 as "no pre-roll" at maturation (:8022), so 0 is a safe sentinel.</summary>
        public static int NextJobId()
        {
            if (MiNextJobId == null)
                return 0;
            try
            {
                return Convert.ToInt32(MiNextJobId.Invoke(null, null));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.NextJobId: " + e.Message); return 0; }
        }

        /// <summary>Step (d): ServiceLevelCodec.Encode(company, tier, jobId) (decompiled :5574).
        /// Returns 0 when the internal method could not be invoked, so the caller can fall back
        /// to the literal layout. NEVER trust the result without <see cref="TryDecodeServiceLevel"/>.</summary>
        public static int EncodeServiceLevel(int companyId, int tier, int jobId)
        {
            if (MiEncode == null)
                return 0;
            try
            {
                return Convert.ToInt32(MiEncode.Invoke(null, new object[] { Enum.ToObject(TCompany, companyId), tier, jobId }));
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.EncodeServiceLevel: " + e.Message); return 0; }
        }

        /// <summary>ServiceLevelCodec.TryDecode(raw, out company, out tier) (decompiled :5604) -
        /// the same call GO's maturation makes first (:8007). Used to VERIFY an encode before it
        /// is written into a live set.</summary>
        public static bool TryDecodeServiceLevel(int raw, out int companyId, out int tier)
        {
            companyId = -1;
            tier = raw;
            if (MiTryDecode == null)
                return false;
            try
            {
                var args = new object[] { raw, Enum.ToObject(TCompany, 0), 0 };
                bool ok = (bool)MiTryDecode.Invoke(null, args);
                if (!ok)
                    return false;
                companyId = Convert.ToInt32(args[1]);
                tier = Convert.ToInt32(args[2]);
                return true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.TryDecodeServiceLevel: " + e.Message); return false; }
        }

        /// <summary>Step (e): PreRollManager.PreRollOnSubmit (decompiled :5371). Rolls each card's
        /// grade and burns its cert NOW, so maturation just applies the stored result.</summary>
        public static bool PreRollJob(GradeCardSubmitSet set, int companyId, bool useCheats, int jobId)
        {
            if (set == null || MiPreRoll == null)
                return false;
            try
            {
                MiPreRoll.Invoke(null, new object[] { set, Enum.ToObject(TCompany, companyId), useCheats, jobId });
                return true;
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.PreRollJob: " + e.Message); return false; }
        }

        /// <summary>Lowest m_ServiceLevel value that is a GO-ENCODED level rather than a vanilla
        /// tier index (ServiceLevelCodec V1_MAGIC, decompiled :5543). Anything at or above this
        /// must not be fed to vanilla's GetGradeCardServiceData for real meaning.</summary>
        public const int EncodedLevelFloor = 10000;

        // ------------------------------------------------------------------
        // SUBMIT SLOT COUNT (GradingSync.ClientSubmit / HostApplyOp).
        //
        // Vanilla GradedCardSubmitSelectScreen.OnPressSubmitButton rebuilds the scratch set
        // with EIGHT empty slots (decompiled GradedCardSubmitSelectScreen.cs :193), and the
        // submit screen only ever shows eight panels. Grading Overhaul changes both halves:
        // GradingSlotExpansionPatches builds up to 52 panels (:1421, :1459) and TRANSPILES
        // OnPressSubmitButton, rewriting that ldc.i4.8 to ldc.i4.s 52
        // (PatchSubmitSlotCount, decompiled-grading Grading Overhaul.decompiled.cs
        // :1498-1515). So with GO a guest really can have up to 52 cards sitting in
        // m_CurrentGradeCardSubmitSet when he presses submit.
        //
        // The named source is GradingSlotExpansionPatches.MAX_SLOTS = 52 (:1338). It is a
        // private const, so C# inlines it at every use site - but a const still exists in
        // metadata as a literal field, which is exactly what GetRawConstantValue reads.
        // Resolve it so a GO release that raises the cap is picked up on its own, and fall
        // back to the literal 52 read off that decompile if the field ever walks away.
        // ------------------------------------------------------------------

        private static readonly Type TSlotExpansion = ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.GradingSlotExpansionPatches", GradingAssembly);

        /// <summary>Vanilla's submit-set slot count. Also the shape every vanilla
        /// GradeCardSubmitSet carries, so it stays the pad target for un-enrolled sets.</summary>
        public const int VanillaSubmitSlots = 8;

        /// <summary>Value of GO 3.4.2's GradingSlotExpansionPatches.MAX_SLOTS (:1338), used
        /// only when the const cannot be read back off the loaded assembly.</summary>
        private const int GoSubmitSlotsFallback = 52;

        private static int _maxSubmitSlots; // resolved once, 0 = not yet resolved

        /// <summary>How many cards one submission may legitimately carry on this machine:
        /// 8 without Grading Overhaul, GO's MAX_SLOTS (52) with it. Bounded to 255 because
        /// the wire carries the card count as a byte.</summary>
        public static int MaxSubmitSlots
        {
            get
            {
                if (!Present)
                    return VanillaSubmitSlots;
                if (_maxSubmitSlots > 0)
                    return _maxSubmitSlots;
                int v = GoSubmitSlotsFallback;
                string how = "GO 3.4.2 decompile literal (MAX_SLOTS unreadable)";
                try
                {
                    var fi = TSlotExpansion == null ? null : ReflectionSurface.OptionalField(TSlotExpansion, "MAX_SLOTS");
                    if (fi != null && fi.IsLiteral && fi.FieldType == typeof(int))
                    {
                        int raw = Convert.ToInt32(fi.GetRawConstantValue());
                        // Never below vanilla (a smaller cap would start dropping cards the
                        // vanilla screen can hold) and never past the byte the wire carries.
                        if (raw >= VanillaSubmitSlots && raw <= 255)
                        {
                            v = raw;
                            how = "GradingSlotExpansionPatches.MAX_SLOTS";
                        }
                        else
                            CoopPlugin.Log.LogWarning($"GradingInterop: GO MAX_SLOTS = {raw} is out of the usable 8..255 range - using {v}");
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.MaxSubmitSlots: " + e.Message); }
                _maxSubmitSlots = v;
                CoopPlugin.Log.LogInfo($"GradingInterop: grading submit slot cap = {v} (from {how})");
                return v;
            }
        }

        /// <summary>Register a received graded card's cert with Grading Overhaul BEFORE it's
        /// added to the album, so GO's anti-cheat prefix finds the cert burned+bound and does
        /// not re-encode it. Idempotent (re-binds the same cert). No-op for non-encoded grades
        /// or when GO is absent. Call on delta/price RECEIVE only, never in a per-frame loop.</summary>
        public static void Remember(CardData card)
        {
            if (card == null || card.cardGrade <= 10 || MiRemember == null)
                return;
            // REFUSE rather than paper over: see CertFreeForCard.
            if (!CertFreeForCard(card))
                return;
            try
            {
                MiRemember.Invoke(null, new object[] { card, card.cardGrade });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("GradingInterop.Remember: " + e.Message); }
        }

        /// <summary>The true encoded grade GO holds for this card (its registry value, or the
        /// card's own cardGrade). Used on SEND so a display's transient 1-10 isn't forwarded.</summary>
        public static int Encoded(CardData card)
        {
            if (card == null)
                return 0;
            if (MiGetEncoded == null)
                return card.cardGrade;
            try
            {
                return (int)MiGetEncoded.Invoke(null, new object[] { card });
            }
            catch (System.Exception e) { Swallow.Log(e); return card.cardGrade; }
        }

        /// <summary>Decode an encoded grade to its real 1-10 value (identity for a bare 1-10).</summary>
        public static int Actual(int encoded)
        {
            if (encoded <= 10 || MiActual == null)
                return encoded;
            try
            {
                return (int)MiActual.Invoke(null, new object[] { encoded });
            }
            catch (System.Exception e) { Swallow.Log(e); return encoded; }
        }

        // ------------------------------------------------------------------
        // CERT IDENTITY + THE CERT-EXISTENCE UNION (CoopCore's graded-album digest).
        //
        // The encoded grade IS the album key (CPlayerData.RemoveGradedCard matches on
        // cardSaveIndex + amount == cardGrade + expansionType + isDestiny, :1552-1571) and it
        // packs the grading company and the cert serial, so the cert is the card's identity.
        // gradedCardIndex is NOT: AddCard sets it to m_GradedCardInventoryList.Count + 1
        // (:1509), which stops being unique the moment any row is removed.
        // ------------------------------------------------------------------

        // internal static void DecodeGradeFull(int, out GradingCompany, out int grade, out int cert)
        // - decompiled-grading :15953. The PUBLIC DecodeGrade wrapper right above it (:15948)
        // deliberately zeroes the cert on the way out, so it is useless here; the internal one is
        // reachable by reflection all the same.
        private static readonly MethodInfo MiDecodeFull = (THelper == null || TCompany == null) ? null
            : ReflectionSurface.OptionalMethod(THelper, "DecodeGradeFull",
                new[] { typeof(int), TCompany.MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() });
        // public static bool IsCheatFlagged(int) - decompiled-grading :16005 (encoded >= 1e9).
        private static readonly MethodInfo MiIsCheat = THelper == null ? null
            : ReflectionSurface.OptionalMethod(THelper, "IsCheatFlagged", new[] { typeof(int) });

        // GO's cert store. The class is public but every member below is INTERNAL, so this is
        // reflection-only even though nothing about it is private.
        //  - bool HasCertBinding(GradingCompany, int)            - decompiled-grading :644
        //  - bool IsCertBoundToCard(GradingCompany, int, CardData) - decompiled-grading :600
        private static readonly Type TSaveMgr =
            ReflectionSurface.OptionalType("TCGCardShopSimulator.GradingOverhaul.GradingOverhaulSaveManager", GradingAssembly);
        private static readonly MethodInfo MiHasBinding = (TSaveMgr == null || TCompany == null) ? null
            : ReflectionSurface.OptionalMethod(TSaveMgr, "HasCertBinding", new[] { TCompany, typeof(int) });
        private static readonly MethodInfo MiIsBoundTo = (TSaveMgr == null || TCompany == null) ? null
            : ReflectionSurface.OptionalMethod(TSaveMgr, "IsCertBoundToCard", new[] { TCompany, typeof(int), typeof(CardData) });

        /// <summary>THE CERT AUTHORITY REFUSAL, and the reason <see cref="Remember"/> can decline
        /// to register a card that arrived over the wire.
        ///
        /// The authority rule is stated in full in Sync/GradingSync's header: the HOST is the sole
        /// minter of certs and the guest adopts them verbatim. This is the one case that rule
        /// cannot fix retroactively - a serial this PC's Grading Overhaul store ALREADY bound to a
        /// DIFFERENT card, from before the rule was enforced (a pre-1.0.42 session where GO's
        /// day-start prefix outranked our block and minted from the guest's own counter, a role
        /// swap, or a sidecar transfer that did not apply).
        ///
        /// GO's own RememberForExternalMod does not defend against it: BurnCert returns false for
        /// an already-burned serial (decompiled-grading :551-559) and RememberForExternalMod
        /// IGNORES that result and calls BindCert anyway (:5921-5922), and BindCert OVERWRITES the
        /// entry (:582-593). So the card that legitimately owned that serial silently loses its
        /// binding, and GO's next AddCard prefix or day-start scan flags THAT card - a card that
        /// was fine - as FAKE. Papering over the collision destroys the resident card.
        ///
        /// So we refuse, loudly, and let the incoming card take the consequences instead: without
        /// the Remember call GO's AddCard prefix sees a cert bound elsewhere (:8526) and re-encodes
        /// the ARRIVING copy as FAKE. That is a visible, reported, one-card outcome affecting the
        /// copy whose provenance is already in doubt, instead of a silent one affecting a card that
        /// was never in doubt. There is deliberately NO automatic re-mint: minting on the guest is
        /// precisely the divergence the authority rule exists to end.
        ///
        /// Unknown or unreadable = permitted. This guard only ever declines on a POSITIVE reading
        /// of a conflicting binding; when GO is absent, the members do not resolve, the grade has
        /// no cert, or anything throws, behaviour is exactly what it was before.</summary>
        /// <summary>(company, cert) keys this session has already printed the full refusal
        /// paragraph for - see the memo note inside <see cref="CertFreeForCard"/>.</summary>
        private static readonly HashSet<long> _refusalWarned = new HashSet<long>();

        /// <summary>Forget per-session refusal diagnostics when a world/session ends.</summary>
        public static void Reset()
        {
            _refusalWarned.Clear();
        }

        private static bool CertFreeForCard(CardData card)
        {
            if (MiHasBinding == null || MiIsBoundTo == null)
                return true;
            // A FAKE-flagged encoding is never burned or bound by GO itself
            // (RememberForExternalMod :5919 skips both), so there is nothing here to protect.
            if (CheatFlagged(card.cardGrade))
                return true;
            int companyId, cert;
            if (!DecodeCert(card.cardGrade, out companyId, out cert))
                return true;
            try
            {
                object company = Enum.ToObject(TCompany, companyId);
                if (!(bool)MiHasBinding.Invoke(null, new object[] { company, cert }))
                    return true;
                if ((bool)MiIsBoundTo.Invoke(null, new object[] { company, cert, card }))
                    return true;
            }
            catch (System.Exception e) { Swallow.Log(e); return true; }

            // MEMOIZED PER CERT, exactly like CoopCore's _priceWarnedKeys and for the same
            // reason. Three of Remember()'s four callers are one-shot per card event, but
            // CoopCore.ApplyRemoteCardPrice is on the repeating price-heal beat - so ONE standing
            // collision used to reprint this whole paragraph every heal, forever, in the very log
            // the refusal exists to make readable. First refusal per cert says everything; after
            // that it is one line. Session-lifetime on purpose: a refused cert stays refused
            // until the rejoin the paragraph asks for, and that rebuilds the process anyway.
            if (!_refusalWarned.Add(((long)companyId << 32) | (uint)cert))
            {
                CoopPlugin.Log.LogWarning("graded cert REFUSED again: certificate " + cert.ToString("D7")
                    + " (company " + companyId + ") on " + CardIdent(card)
                    + " - same collision as reported earlier this session, still not registered");
                return false;
            }

            CoopPlugin.Log.LogWarning("graded cert REFUSED: certificate " + cert.ToString("D7")
                + " (company " + companyId + ") arrived on " + CardIdent(card)
                + ", but this PC's Grading Overhaul store already has that certificate bound to a"
                + " DIFFERENT card. Registering it would silently steal the binding from the card"
                + " that holds it here and make Grading Overhaul flag THAT card as a fake, so the"
                + " incoming copy is not registered - Grading Overhaul will mark the ARRIVING copy"
                + " instead. This is not repairable in place and it is not re-minted on purpose:"
                + " the host is the only machine allowed to issue certificates. The two save stores"
                + " have drifted apart; the clean reset is to rejoin so the host's grading store"
                + " transfers again.");
            return false;
        }

        /// <summary>Short, log-safe identity for the refusal line above. Deliberately local to
        /// this file: CoopCore's CardIdent knows about the co-op session, and this can be reached
        /// from a card path with no session at all.</summary>
        private static string CardIdent(CardData card)
        {
            if (card == null)
                return "(null card)";
            try
            {
                return card.monsterType + "/" + card.expansionType
                    + (card.isFoil ? " foil" : "") + (card.isDestiny ? " destiny" : "")
                    + " border " + (int)card.borderType;
            }
            catch (System.Exception e) { Swallow.Log(e); return "(unreadable card)"; }
        }

        /// <summary>Split an encoded grade into its grading company ordinal and cert serial.
        /// False (and cert 0) for a bare 1-10 grade, when Grading Overhaul is absent, or when the
        /// decode member could not be resolved - callers must then simply skip cert reasoning
        /// rather than guess, since a fabricated cert would produce false collision reports.</summary>
        public static bool DecodeCert(int encoded, out int companyId, out int cert)
        {
            companyId = -1;
            cert = 0;
            if (encoded <= 10 || MiDecodeFull == null)
                return false;
            try
            {
                var args = new object[] { encoded, Enum.ToObject(TCompany, 0), 0, 0 };
                MiDecodeFull.Invoke(null, args);
                companyId = Convert.ToInt32(args[1]);
                cert = Convert.ToInt32(args[3]);
                return cert > 0;
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        /// <summary>(company, cert) packed exactly the way GO's own BuildExistingCertSet packs it
        /// (decompiled-grading :8801-8880) - a serial is only unique WITHIN a company. 0 means
        /// "no usable cert", never a valid key.</summary>
        public static long CertKey(int encoded)
        {
            int company, cert;
            if (!DecodeCert(encoded, out company, out cert))
                return 0L;
            return ((long)company << 32) | (uint)cert;
        }

        /// <summary>True when GO has stamped this grade with its FAKE encoding (+1,000,000,000).
        /// Worth naming in a divergence report: a FAKE-flagged row on one side and a clean cert
        /// on the other is the duplicate-cert sweep having fired, not a missing card.</summary>
        public static bool CheatFlagged(int encoded)
        {
            if (MiIsCheat == null)
                return false;
            try
            {
                return (bool)MiIsCheat.Invoke(null, new object[] { encoded });
            }
            catch (System.Exception e) { Swallow.Log(e); return false; }
        }

        /// <summary>One graded card as the digest identifies it. Everything
        /// CPlayerData.GetCardSaveIndex reads (monsterType, borderType, isFoil - :795-811) plus
        /// the expansion, isDestiny and the encoded grade. Border and foil are NOT decoration:
        /// they are half of cardSaveIndex, so a digest without them both under-reports (two
        /// border variants of one monster collapse into one key) and, worse, would make the adopt
        /// path file a borrowed card as border 0 / non-foil - a different card.</summary>
        public struct GradedEntry
        {
            public ECardExpansionType Expansion;
            public EMonsterType Monster;
            public ECardBorderType Border;
            public bool IsFoil;
            public bool IsDestiny;
            public int Encoded;

            /// <summary>Local bookkeeping only - NEVER written to or read from the wire (see
            /// CoopCore.WriteGradedDigest, which writes the six identity fields and nothing else).
            /// Set by CoopCore.GradedAdopt on a candidate it refused, so the entry can stay in the
            /// per-peer diff for the report while being excluded from the offer count.</summary>
            public bool Refused;

            /// <summary>Set-membership key for the diff. String rather than a packed hash for the
            /// same reason CoopCore.CardPriceKey is one: it is also what the log lines print.</summary>
            public string Key
            {
                get
                {
                    return (int)Expansion + ":" + (int)Monster + ":" + (int)Border
                        + ":" + (IsFoil ? 1 : 0) + (IsDestiny ? 1 : 0) + ":" + Encoded;
                }
            }

            public CardData ToCard()
            {
                return new CardData
                {
                    expansionType = Expansion,
                    monsterType = Monster,
                    borderType = Border,
                    isFoil = IsFoil,
                    isDestiny = IsDestiny,
                    isChampionCard = false,
                    isNew = false,
                    cardGrade = Encoded,
                    gradedCardIndex = 0, // never an identity - see the block comment above
                };
            }
        }

        /// <summary>Every place a graded cert can legitimately live on THIS machine, deduped.
        ///
        /// THE RULE - AND IT IS THE OPPOSITE OF WHAT THE 1.0.41 COMMENT ARGUED HERE:
        /// this union may contain ONLY containers that BOTH PEERS MIRROR. A container that
        /// exists on one PC alone must never be counted, however much more "complete" it would
        /// make the local picture look.
        ///
        /// The 1.0.41 comment reasoned about ONE consumer and shipped a bug; 1.0.42's first cut
        /// then fixed that consumer and broke the other one, so both are named here. Two
        /// questions are asked of this data and they fail in OPPOSITE directions:
        ///  - "Do I already hold this cert?" - the adopt REFUSAL (CoopCore.GradedAdopt's cert
        ///    guard, and the certHeldHere / reEncoded / CERT COLLISION half of
        ///    CoopCore.CompareGradedDigests). That wants the widest view available: a miss offers
        ///    an add for a card that is already here, AddCard puts a second row on one cert, and
        ///    Grading Overhaul's duplicate-cert sweep rewrites BOTH rows to the FAKE encoding.
        ///  - "Is my peer missing this?" - the DIFF (the mineKeys / oursOnly / peerOnlyTotal half
        ///    of CompareGradedDigests, and everything SendGradedDigest puts on the wire). That is
        ///    a comparison against the PEER's union, and a comparison only means anything when
        ///    the two sides were built from the same containers.
        /// A local-only container satisfies the first and destroys the second - and the second is
        /// the one that ends up MUTATING CARDS, because the diff is what the F2 adopt button is
        /// built from.
        ///
        /// SO THERE ARE TWO WALKS, NOT ONE. This method is the NARROW one and answers only the
        /// second question. <see cref="LocalOnlyGradedCerts"/> is the local-only remainder, and
        /// the guard callers use the UNION of the two - see the contract on that method.
        /// Behind BOTH of them sits <see cref="CertFreeForCard"/>, which asks Grading Overhaul's
        /// own store the same question at Remember() time and is the last line that covers the
        /// refusal direction: a future reader who deletes that guard is also deleting the
        /// backstop for every cert this union cannot see.
        ///
        /// Worked through, because this is the exact 1.0.41 defect: put the player's HAND in here
        /// and every graded card in motion reads as a genuine two-sided divergence. Picking a slab
        /// out of the binder calls CPlayerData.RemoveGradedCard, which IS mirrored
        /// (GamePatches.RemoveGradedCardPostfix -> CoopCore.ForwardGradedRemoval), so the peer's
        /// album really does drop it, while the local hand keeps it in this union. The old comment
        /// feared an album-only digest would "cry wolf about a card the other player is holding" -
        /// but the PEER's hand is not in the PEER's digest either. With the hand gone from BOTH
        /// sides a held card is absent on both and the digests MATCH. Including it is what creates
        /// the cry, and then the button offers a card that was never missing, the adopt AddCards
        /// it, the holder puts the real one away, and GO flags both copies FAKE. A diagnostic
        /// destroyed a real card.
        ///
        /// SO THE TRADE IS NOT SYMMETRIC AND MUST NEVER BE TREATED AS IF IT WERE:
        ///   a FALSE NEGATIVE - a card in transit is briefly invisible, so a genuine loss is
        ///   reported one cycle later - is HARMLESS;
        ///   a FALSE POSITIVE - a local-only container makes this PC a strict superset of the
        ///   peer - is DESTRUCTIVE.
        /// Never add a container to this walk without first naming the module that mirrors it.
        ///
        /// BANNED FROM THIS WALK - AND MANDATORY IN THE GUARD WALK. Both containers were in the
        /// 1.0.41 union, both ARE the diff bug, and both are REQUIRED by the refusal; they live in
        /// <see cref="LocalOnlyGradedCerts"/> for exactly that reason. Do not re-add them here and
        /// do not delete them from there. The reason they are mandatory in the guard is Grading
        /// Overhaul itself, not a hunch: AntiCheat_AddCard_Patch.Prefix scans m_HoldCardDataList
        /// for a duplicate (company, cert) and, on a hit, re-encodes BOTH the held card and the
        /// arriving one as FAKE (decompiled-grading :8554-8573), and GO's own BuildExistingCertSet
        /// counts the hand as well (:8801-8880). A cert we cannot see but GO can is a cert our
        /// refusal waves through and GO then punishes AT THE AddCard - two real cards flagged by
        /// one press of an adopt button. The guard has to be at least as wide as GO's own view.
        ///  - CPlayerData.m_HoldCardDataList - the local player's HAND. Nothing mirrors it; the
        ///    name appears nowhere else in src/. Every "received a graded card" passes through it
        ///    (InteractablePackagingBox_Card.OnPressOpenBox :110-118 ->
        ///    InteractionPlayerController.AddHoldCard :3598), and so does every binder pull
        ///    (CollectionBinderFlipAnimCtrl.cs:465-472).
        ///  - CPlayerData.m_CurrentGradeCardSubmitSet - the submit screen's scratch buffer, which
        ///    is each player's own UI state (see GradingSync) and never on the wire. Staging a
        ///    graded card there RemoveGradedCards it off every peer while it sits somewhere only
        ///    this PC can see: the same shape as the hand.
        /// Being WIDER than Grading Overhaul's own BuildExistingCertSet (decompiled-grading
        /// :8801-8880) is still fine wherever the extra container is mirrored - GO missing a
        /// container merely UNDER-flags a cheat, while we have to be right about BOTH sides - so
        /// this is not a trim "for parity" with GO. It is a trim to what the wire actually shares.
        ///
        /// WHAT IS IN, AND WHO MIRRORS IT:
        ///  - m_GradedCardInventoryList, the album - CardDelta / GradedRemove.
        ///  - m_GradeCardInProgressList, jobs at the grader - GradingSync's pending-list mirror.
        ///  - card shelves and card/item combi shelves - CardShelfSync.
        ///  - card packaging (graded-return) boxes - CardBoxOps.
        ///  - card storage shelves, bulk donation boxes, auto pack openers - ContainerSync.
        ///
        /// THE FIVE WORLD CLASSES ARE READ LIVE, never from CPlayerData's save-time snapshot
        /// lists (m_CardShelfSaveDataList and friends). Those lists are written only by
        /// ShelfManager.SaveInteractableObjectData, which fires from unrelated UI - PauseScreen
        /// (:25), the end-of-day report (:64), the shop screens, an Alt/Backspace hotkey
        /// (InteractionPlayerController.cs:318) - so each PC's snapshot is stale by a DIFFERENT
        /// amount and the pair diverge for reasons that have nothing to do with cards. That is
        /// the same rule as above wearing a different hat: both peers have to be describing the
        /// same world at the same moment, and only the live managers do.
        ///
        /// Only encoded grades (&gt; 10) are collected: a bare 1-10 vanilla grade carries no cert,
        /// so it has no stable identity to diff and every one of them would "collide" on cert 0.
        /// With Grading Overhaul absent this therefore returns an empty list, which self-gates the
        /// whole diagnostic off rather than needing a Present check (and Present has a one-shot
        /// logging side effect that does not belong on a 45s timer).
        ///
        /// NULL MEANS "CANNOT CHECK NOW", AND IT IS NOT THE SAME AS EMPTY. Five of the seven
        /// sources are live scene objects that fill progressively during a world load, so during
        /// that window the walk would return a SHORT union - the direction that fabricates adopt
        /// offers on the peer and disarms the refusal here. There is no gate anywhere else that
        /// covers it: InGameLevel() stays TRUE through the client reload screen and through a
        /// host mid-session save load (CoopCore's note on that), so the digest timer and the F2
        /// button are both live throughout. We therefore FAIL CLOSED on the shelf manager's own
        /// readiness flag (ShelfManager.m_FinishLoadingObjectData, the same flag the game gates
        /// its own object save/load on) and every caller must treat null as "skip this cycle" -
        /// never as an empty album.</summary>
        public static List<GradedEntry> BuildGradedCertInventory()
        {
            var sm0 = Shelves();
            if (sm0 == null || !sm0.m_FinishLoadingObjectData)
                return null;

            var list = new List<GradedEntry>();
            var seen = new HashSet<string>();

            // PER-CONTAINER-CLASS try/catch, never one catch around the lot. A throw partway
            // through used to abandon every container after it, and a truncated union is exactly
            // the false negative that makes cards which ARE here look missing - the direction
            // that fabricates adopt offers. One unreadable class now costs one class, loudly.
            Step("graded album", () => AddAllCompact(list, seen, CPlayerData.m_GradedCardInventoryList));

            Step("grading jobs in progress", () =>
            {
                var inProgress = CPlayerData.m_GradeCardInProgressList;
                if (inProgress != null)
                    for (int i = 0; i < inProgress.Count; i++)
                        AddAll(list, seen, inProgress[i]?.m_CardDataList);
            });

            Step("card shelves", () =>
            {
                var sm = Shelves();
                var shelves = sm == null ? null : sm.m_CardShelfList;
                if (shelves != null)
                    for (int i = 0; i < shelves.Count; i++)
                        AddShelfCards(list, seen, shelves[i]);
            });

            Step("card/item combi shelves", () =>
            {
                var sm = Shelves();
                var combi = sm == null ? null : sm.m_CardItemCombiShelfList;
                if (combi != null)
                    for (int i = 0; i < combi.Count; i++)
                        AddShelfCards(list, seen, combi[i]);
            });

            Step("card packaging boxes", () =>
            {
                var rm = Restock();
                var boxes = rm == null ? null : rm.m_CardPackagingBoxList;
                if (boxes != null)
                    for (int i = 0; i < boxes.Count; i++)
                        if (boxes[i] != null)
                            AddAll(list, seen, boxes[i].GetCardDataList());
            });

            Step("card storage shelves", () =>
            {
                var sm = Shelves();
                var storage = sm == null ? null : sm.m_CardStorageShelfList;
                if (storage != null)
                    for (int i = 0; i < storage.Count; i++)
                        if (storage[i] != null)
                            AddAllCompact(list, seen, storage[i].GetCompactCardDataAmountList());
            });

            Step("bulk donation boxes", () =>
            {
                var sm = Shelves();
                var donation = sm == null ? null : sm.m_BulkDonationBoxList;
                if (donation != null)
                    for (int i = 0; i < donation.Count; i++)
                        if (donation[i] != null)
                            AddAllCompact(list, seen, donation[i].GetCompactCardDataAmountList());
            });

            Step("auto pack openers", () =>
            {
                var sm = Shelves();
                var openers = sm == null ? null : sm.m_AutoPackOpenerList;
                if (openers != null)
                    for (int i = 0; i < openers.Count; i++)
                        if (openers[i] != null)
                            AddAllCompact(list, seen, openers[i].GetCompactCardDataAmountList());
            });

            return list;
        }

        /// <summary>The LOCAL-ONLY graded certs: the two containers
        /// <see cref="BuildGradedCertInventory"/> is forbidden to walk, walked on purpose.
        ///
        /// WHAT THIS IS FOR, AND WHAT IT MUST NEVER TOUCH. This list answers "do I already hold
        /// this cert?" and NOTHING ELSE. It must never reach the wire, never seed mineKeys in the
        /// diff, and never influence oursOnly / peerOnlyTotal - those are comparisons against a
        /// peer that cannot see these containers, and feeding them a local-only card is the exact
        /// 1.0.41 defect (a card in your hand read as a genuine two-sided divergence, the button
        /// offered it, and Grading Overhaul flagged both copies FAKE). Callers therefore take the
        /// UNION of this and the narrow walk for mineByCert only, per the contract there.
        ///
        /// Why it has to exist at all: Grading Overhaul's AntiCheat_AddCard_Patch.Prefix scans
        /// CPlayerData.m_HoldCardDataList for a duplicate (company, cert) and re-encodes BOTH the
        /// held card and the arriving one as FAKE (decompiled-grading :8554-8573); its own cert
        /// set includes the hand too (:8801-8880). A guard narrower than GO's view is a guard
        /// that lets the player press a button GO then punishes.
        ///
        /// These are CPlayerData snapshot lists, not live scene objects, so unlike the narrow
        /// walk there is nothing here to gate on world load - they are whole the moment the save
        /// is. Same per-container Step policy: one unreadable container costs one container,
        /// loudly, because a miss here is a miss in the direction that lets a bad add through.</summary>
        public static List<GradedEntry> LocalOnlyGradedCerts()
        {
            var list = new List<GradedEntry>();
            var seen = new HashSet<string>();

            Step("the player's hand", () => AddAll(list, seen, CPlayerData.m_HoldCardDataList));

            Step("the grading submit screen's scratch set", () =>
            {
                var set = CPlayerData.m_CurrentGradeCardSubmitSet;
                if (set != null)
                    AddAll(list, seen, set.m_CardDataList);
            });

            return list;
        }

        /// <summary>One container class of either walk, isolated - shared by
        /// <see cref="BuildGradedCertInventory"/> and <see cref="LocalOnlyGradedCerts"/>. See the
        /// note at the top of the first: an unreadable container is a false NEGATIVE, which is
        /// the harmless direction for the diff but the DANGEROUS one for the adopt refusal, so it
        /// is warned about by name rather than swallowed.</summary>
        private static void Step(string what, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("GradingInterop: could not read "
                    + what + " - " + e.Message
                    + " (graded cards in that container look MISSING on this PC for this check; if"
                    + " this repeats, treat the graded album report as unreliable this session)");
            }
        }

        /// <summary>Cached ShelfManager for the live-world walk.
        ///
        /// NEVER CSingleton&lt;ShelfManager&gt;.Instance: that getter CREATES an empty fake
        /// manager when the real one does not exist yet and the fake then shadows the real one for
        /// the rest of the session (the 1.0.11 store-mirror field report; the same warning is
        /// written out at Sync/ItemBoxFamily.cs and Sync/WorldSync.cs). Unity's fake-null makes this
        /// cache self-invalidating across a scene change, so there is nothing to reset.</summary>
        private static ShelfManager _shelfMgr;
        private static ShelfManager Shelves()
        {
            if (_shelfMgr == null)
                _shelfMgr = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _shelfMgr;
        }

        /// <summary>Cached RestockManager, resolved the same way and for the same reason -
        /// RestockManager.GetCardPackagingBoxList() goes through CSingleton and would carry the
        /// fake-manager hazard into a 45s timer.</summary>
        private static RestockManager _restockMgr;
        private static RestockManager Restock()
        {
            if (_restockMgr == null)
                _restockMgr = UnityEngine.Object.FindObjectOfType<RestockManager>();
            return _restockMgr;
        }

        /// <summary>The cards on one live card shelf (or card/item combi shelf, which derives from
        /// it). Slot 0 of each compartment only: that is the representative the game's own save
        /// takes (ShelfManager.SaveInteractableObjectData) AND the one CardShelfSync mirrors, so it
        /// is exactly the part of a shelf that both peers agree about. Reading deeper into
        /// m_StoredCardList would count state no module mirrors - the banned direction.</summary>
        private static void AddShelfCards(List<GradedEntry> list, HashSet<string> seen, CardShelf shelf)
        {
            if (shelf == null)
                return;
            var comps = shelf.GetCardCompartmentList();
            if (comps == null)
                return;
            for (int i = 0; i < comps.Count; i++)
            {
                var comp = comps[i];
                if (comp == null || comp.m_StoredCardList == null || comp.m_StoredCardList.Count == 0)
                    continue;
                var card3d = comp.m_StoredCardList[0];
                if (card3d == null || card3d.m_Card3dUI == null || card3d.m_Card3dUI.m_CardUI == null)
                    continue;
                Add(list, seen, card3d.m_Card3dUI.m_CardUI.GetCardData());
            }
        }

        private static void AddAll(List<GradedEntry> list, HashSet<string> seen, List<CardData> cards)
        {
            if (cards == null)
                return;
            for (int i = 0; i < cards.Count; i++)
                Add(list, seen, cards[i]);
        }

        /// <summary>Compact (save-shaped) rows, where the encoded grade rides in <c>amount</c>.
        /// Per-ROW try/catch, never per-list: one row that throws must not silently truncate the
        /// rest of a container's contents, because a truncated container is precisely the miss
        /// that fabricates an adopt offer.
        ///
        /// What throws is NOT a divide by zero - an earlier version of this comment claimed
        /// GetCardAmountPerMonsterType returns 0 for an expansion this install has no data for,
        /// and that is false. It initialises <c>num = 6</c> BEFORE its switch, every case assigns
        /// 6 (or 1 for Ghost), and there is no default arm
        /// (decompiled/CPlayerData.cs:692-721, the init at :694), so it returns 6 or 12 even for an
        /// expansion it has never heard of. GetGradedCardData cannot divide by zero.
        ///
        /// The real hazard is an out-of-range INDEX. GetGradedCardData resolves the monster through
        /// GetMonsterTypeFromCardSaveIndex, which does
        /// <c>InventoryBase.GetShownMonsterList(exp)[cardSaveIndex / perType]</c> (:790-793), and
        /// that list falls back to the TETRAMON list for any expansion this install lacks
        /// (decompiled/InventoryBase.cs:290-308, the default arm). A row saved against a card set
        /// the other PC has and this one does not therefore indexes the wrong, usually shorter,
        /// list: ArgumentOutOfRangeException when the index runs off the end, and a confidently
        /// wrong monster when it happens to land inside. It can also NullReference through
        /// <c>CSingleton&lt;InventoryBase&gt;.Instance</c> before any of that.
        ///
        /// That reading is verified against VANILLA and Grading Overhaul only - GO's single
        /// reference to GetCardAmountPerMonsterType is a read (decompiled-grading :7132), it does
        /// not patch it. EPL is not visible from this repo and could in principle patch it, so do
        /// not lean on "never zero" as a guarantee anywhere: the catch, not the invariant, is what
        /// makes this loop safe either way, and it stays whichever hazard is the live one.</summary>
        private static void AddAllCompact(List<GradedEntry> list, HashSet<string> seen,
            List<CompactCardDataAmount> rows)
        {
            if (rows == null)
                return;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.amount <= 10)
                    continue;
                try
                {
                    Add(list, seen, CPlayerData.GetGradedCardData(row));
                }
                catch (System.Exception e) { Swallow.Log(e); }
            }
        }

        private static void Add(List<GradedEntry> list, HashSet<string> seen, CardData c)
        {
            if (c == null || c.cardGrade <= 10)
                return;
            // A BLANKED SUBMIT SLOT IS NOT A CARD. GradedCardSubmitSelectScreen.OnCloseScreen
            // hands every staged card back and then sets monsterType = EMonsterType.None on the
            // slot it abandoned (decompiled/GradedCardSubmitSelectScreen.cs:92) - it does NOT
            // clear cardGrade, so the slot keeps its encoded value. LocalOnlyGradedCerts walks
            // exactly that list, so without this the abandoned slot entered the digest as a GHOST
            // ENTRY: a real certificate on a card with no monster. It then collided under SameCard
            // with the genuine card that still holds that cert, and the check reported a CERT
            // COLLISION - the one category that means "Grading Overhaul is about to call both
            // copies FAKE" - for a slot that no longer holds anything.
            if (c.monsterType == EMonsterType.None)
                return;

            var e = new GradedEntry
            {
                Expansion = c.expansionType,
                Monster = c.monsterType,
                Border = c.borderType,
                IsFoil = c.isFoil,
                IsDestiny = c.isDestiny,
                Encoded = c.cardGrade,
            };
            // Deduped because this is an EXISTENCE set, not a count. The duplicate rows GO's
            // anti-cheat sweep leaves behind would otherwise inflate one side's total and make a
            // pure set difference read as a count difference.
            if (seen.Add(e.Key))
                list.Add(e);
        }
    }
}
