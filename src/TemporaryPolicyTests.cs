using System.Security.Cryptography;
using System.Text.Json;
namespace WishGuard;

// All credentials and stores here belong to isolated self-test fixtures.
public static class TemporaryPolicyTests
{
    const long Now = 1900000000;
    const string Reason = "Approved isolated character-release fixture.";

    static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Temporary credential assertion failed.");
    }

    static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); } catch { rejected = true; }
        Expect(rejected);
    }

    static GuardState Fresh() => new() { Committed = true, Resources = new(0, 0, Now) };
    static GuardState Clone(GuardState state) => JsonSerializer.Deserialize<GuardState>(JsonSerializer.Serialize(state))!;
    static Grant Temporary(GuardState state, string id = "temporary-test") =>
        new("wishguard-v2", state.InstallId, state.PolicyId, state.Nonce, id, Now, Now + 86400,
            0, "temporary", Rules.TemporaryDurationSeconds, Reason);
    static Grant Permanent(GuardState state) =>
        new("wishguard-v2", state.InstallId, state.PolicyId, state.Nonce, "permanent-test", Now, Now + 86400,
            600, "permanent");

    public static void Run(Action<string, Action> check, string data)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string testRoot = Path.Combine(data, "temporary-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        string Sign(Grant grant)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(grant);
            return JsonSerializer.Serialize(new Envelope(Rules.Encode(payload), Rules.Encode(key.SignData(payload,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))));
        }
        string Tamper(string token, Grant replacement)
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(token)!;
            return JsonSerializer.Serialize(envelope with { Payload = Rules.Encode(JsonSerializer.SerializeToUtf8Bytes(replacement)) });
        }
        GuardState Active(string id = "temporary-test")
        {
            var state = Fresh();
            Rules.ActivateTemporary(state, Sign(Temporary(state, id)), publicKey, Now);
            return state;
        }
        StateStore Store(string name)
        {
            var store = new StateStore(Path.Combine(testRoot, name));
            store.State.Committed = true; store.State.Resources = new(0, 0, Now); store.Save();
            return store;
        }

        check("temporary pass accepts zero resources and resolves import mode", () =>
        {
            var state = Fresh(); string token = Sign(Temporary(state));
            var grant = Rules.ValidateForImport(token, publicKey, state, Now);
            Expect(grant.UnlockMode == "temporary" && grant.VerifiedPulls == 0 && grant.DurationSeconds == 1800 &&
                grant.ApprovalReason == Reason && state.Resources?.Pulls == 0);
        });
        check("temporary duration is fixed at thirty minutes", () =>
        {
            Expect(Rules.TemporaryDurationSeconds == 1800);
            var state = Fresh(); var grant = Temporary(state);
            foreach (long seconds in new long[] { 0, 1799, 1801, long.MaxValue })
                Reject(() => Rules.ValidateTemporary(Sign(grant with { DurationSeconds = seconds }), publicKey, state, Now, true));
        });
        check("temporary approval reason is required and length limited", () =>
        {
            var state = Fresh(); var grant = Temporary(state);
            foreach (string reason in new[] { "", " \t\r\n", new string('a', 501) })
                Reject(() => Rules.ValidateTemporary(Sign(grant with { ApprovalReason = reason }), publicKey, state, Now, true));
            Expect(Rules.ValidateTemporary(Sign(grant with { ApprovalReason = new string('a', 500) }), publicKey, state, Now, true).ApprovalReason.Length == 500);
        });
        check("temporary verified pulls must remain zero", () =>
        {
            var state = Fresh(); var grant = Temporary(state);
            foreach (long pulls in new long[] { -1, 1, 600 })
                Reject(() => Rules.ValidateTemporary(Sign(grant with { VerifiedPulls = pulls }), publicKey, state, Now, true));
        });
        check("temporary redemption window is at most twenty-four hours", () =>
        {
            var state = Fresh(); var grant = Temporary(state);
            Expect(Rules.ValidateTemporary(Sign(grant), publicKey, state, Now, true).RedeemBefore == Now + 86400);
            foreach (long deadline in new long[] { Now - 1, Now, Now + 86401 })
                Reject(() => Rules.ValidateTemporary(Sign(grant with { RedeemBefore = deadline }), publicKey, state, Now, true));
        });
        check("temporary redemption deadline rejects its exact boundary", () =>
        {
            var state = Fresh(); string token = Sign(Temporary(state));
            Expect(Rules.ValidateTemporary(token, publicKey, state, Now + 86399, true).UnlockMode == "temporary");
            Reject(() => Rules.ValidateTemporary(token, publicKey, state, Now + 86400, true));
        });
        check("temporary issue time accepts sixty seconds and rejects sixty-one", () =>
        {
            var state = Fresh(); var grant = Temporary(state);
            Expect(Rules.ValidateTemporary(Sign(grant with { IssuedAt = Now + 60, RedeemBefore = Now + 86460 }), publicKey, state, Now, true).IssuedAt == Now + 60);
            Reject(() => Rules.ValidateTemporary(Sign(grant with { IssuedAt = Now + 61, RedeemBefore = Now + 86461 }), publicKey, state, Now, true));
            foreach (long issued in new long[] { -1, 0 })
                Reject(() => Rules.ValidateTemporary(Sign(grant with { IssuedAt = issued, RedeemBefore = 100 }), publicKey, state, Now, true));
        });
        check("temporary pass remains bound to device policy and enrollment", () =>
        {
            var state = Fresh(); var grant = Temporary(state);
            foreach (Grant wrong in new[] { grant with { InstallId = "another" }, grant with { PolicyId = "another" },
                grant with { Nonce = "another" }, grant with { Version = "wishguard-v1" }, grant with { Id = " " } })
                Reject(() => Rules.ValidateTemporary(Sign(wrong), publicKey, state, Now, true));
            state.Committed = false;
            Reject(() => Rules.ValidateTemporary(Sign(grant), publicKey, state, Now, true));
        });
        check("temporary signed fields cannot be modified without resigning", () =>
        {
            var state = Fresh(); var grant = Temporary(state); string token = Sign(grant);
            foreach (Grant changed in new[] { grant with { ApprovalReason = "Changed approval" }, grant with { DurationSeconds = 1801 },
                grant with { RedeemBefore = Now + 3600 }, grant with { Id = "different-id" }, grant with { VerifiedPulls = 600 } })
                Reject(() => Rules.ValidateTemporary(Tamper(token, changed), publicKey, state, Now, true));
        });
        check("temporary pass rejects a different verification key or signature", () =>
        {
            var state = Fresh(); string token = Sign(Temporary(state));
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Reject(() => Rules.ValidateTemporary(token, Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()), state, Now, true));
            var envelope = JsonSerializer.Deserialize<Envelope>(token)!; byte[] signature = Rules.Decode(envelope.Signature); signature[^1] ^= 1;
            Reject(() => Rules.ValidateTemporary(JsonSerializer.Serialize(envelope with { Signature = Rules.Encode(signature) }), publicKey, state, Now, true));
        });
        check("temporary and permanent import endpoints cannot be mixed", () =>
        {
            var state = Fresh(); string temporary = Sign(Temporary(state)), permanent = Sign(Permanent(state));
            Reject(() => Rules.Validate(temporary, publicKey, state, Now, true));
            Reject(() => Rules.ValidateTemporary(permanent, publicKey, state, Now, true));
            Expect(Rules.ValidateForImport(permanent, publicKey, state, Now).UnlockMode == "permanent");
            Reject(() => Rules.ValidateForImport(Sign(Temporary(state) with { UnlockMode = "unknown" }), publicKey, state, Now));
        });
        check("temporary receipt cannot permanently finish a plan", () =>
        {
            var state = Active(); state.PermanentPass = state.TemporaryPass;
            Expect(!Rules.Permanent(state, publicKey) && !Rules.Ended(state, publicKey) && Rules.TemporaryActive(state, publicKey, Now));
        });
        check("permanent credential cannot masquerade as temporary receipt", () =>
        {
            var state = Fresh(); var grant = Permanent(state); state.TemporaryPass = Sign(grant);
            state.TemporaryActivatedAt = Now; state.TemporaryUntil = Now + 1800; state.UsedPasses.Add(grant.Id);
            Expect(!Rules.TemporaryActive(state, publicKey, Now));
        });
        check("activation preserves the six-hundred-pull permanent plan", () =>
        {
            var state = Fresh(); Rules.RequestRecovery(state, Now - 100);
            long? recovery = state.RecoveryAt; var resources = state.Resources;
            var grant = Rules.ActivateTemporary(state, Sign(Temporary(state)), publicKey, Now);
            Expect(state.TargetPulls == 600 && state.UnlockMode == "permanent" && state.Committed && state.StartWithWindows &&
                state.Resources == resources && state.RecoveryAt == recovery && !state.RecoveryFinished && state.PermanentPass == null &&
                state.TemporaryActivatedAt == Now && state.TemporaryUntil == Now + 1800 && state.LastSeen == Now &&
                state.UsedPasses.SequenceEqual(new[] { grant.Id }) && !Rules.Ended(state, publicKey));
        });
        check("temporary active interval includes activation and excludes exact expiry", () =>
        {
            var state = Active();
            Expect(!Rules.TemporaryActive(state, publicKey, Now - 1) && Rules.TemporaryActive(state, publicKey, Now) &&
                Rules.TemporaryActive(state, publicKey, Now + 1799) && !Rules.TemporaryActive(state, publicKey, Now + 1800) &&
                !Rules.TemporaryActive(state, publicKey, Now + 1801));
        });
        check("late redemption still receives only thirty minutes after activation", () =>
        {
            var state = Fresh(); string token = Sign(Temporary(state)); long activation = Now + 86399;
            Rules.ActivateTemporary(state, token, publicKey, activation);
            Expect(state.TemporaryUntil == activation + 1800 && Rules.TemporaryActive(state, publicKey, activation + 1799) &&
                !Rules.TemporaryActive(state, publicKey, activation + 1800));
        });
        check("active temporary pass cannot be reactivated or extended", () =>
        {
            var state = Active(); string token = state.TemporaryPass!; long? deadline = state.TemporaryUntil;
            Reject(() => Rules.ActivateTemporary(state, token, publicKey, Now + 1));
            Expect(state.TemporaryUntil == deadline && state.TemporaryActivatedAt == Now && state.UsedPasses.Count == 1);
        });
        check("consumed temporary pass remains rejected after expiry", () =>
        {
            var state = Active(); string token = state.TemporaryPass!;
            Reject(() => Rules.ValidateTemporary(token, publicKey, state, Now + 1800, true));
            Reject(() => Rules.ActivateTemporary(state, token, publicKey, Now + 1800));
            Expect(state.TemporaryUntil == Now + 1800 && state.UsedPasses.Count == 1);
        });
        check("new temporary pass cannot stack onto an active pass", () =>
        {
            var state = Active(); string next = Sign(Temporary(state, "temporary-next"));
            Reject(() => Rules.ActivateTemporary(state, next, publicKey, Now + 1));
            Expect(state.TemporaryUntil == Now + 1800 && state.TemporaryActivatedAt == Now && !state.UsedPasses.Contains("temporary-next"));
        });
        check("new approved temporary pass may activate after previous expiry", () =>
        {
            var state = Active(); string next = Sign(Temporary(state, "temporary-next"));
            Rules.ActivateTemporary(state, next, publicKey, Now + 1800);
            Expect(state.TemporaryPass == next && state.TemporaryActivatedAt == Now + 1800 && state.TemporaryUntil == Now + 3600 &&
                state.UsedPasses.SequenceEqual(new[] { "temporary-test", "temporary-next" }) && Rules.TemporaryActive(state, publicKey, Now + 1800));
        });
        check("completed recovery cannot use an old receipt or activate a new one", () =>
        {
            var state = Active(); state.RecoveryFinished = true;
            Expect(!Rules.TemporaryActive(state, publicKey, Now));
            Reject(() => Rules.ActivateTemporary(state, Sign(Temporary(state, "temporary-next")), publicKey, Now));
        });
        check("permanent completion cannot use or activate temporary credentials", () =>
        {
            var state = Active(); state.PermanentPass = Sign(Permanent(state));
            Expect(Rules.Ended(state, publicKey) && !Rules.TemporaryActive(state, publicKey, Now));
            Reject(() => Rules.ActivateTemporary(state, Sign(Temporary(state, "temporary-next")), publicKey, Now));
        });
        check("temporary receipt fails even a one-second clock rollback", () =>
        {
            var state = Active(); state.LastSeen = Now + 100;
            Expect(!Rules.ClockRolledBack(state, Now + 99) && !Rules.TemporaryActive(state, publicKey, Now + 99) &&
                Rules.TemporaryActive(state, publicKey, Now + 100));
        });
        check("temporary activation rejects clock rollback without consuming a pass", () =>
        {
            var state = Fresh(); state.LastSeen = Now + 1; string token = Sign(Temporary(state));
            Reject(() => Rules.ActivateTemporary(state, token, publicKey, Now));
            Expect(state.TemporaryPass == null && state.TemporaryUntil == null && state.UsedPasses.Count == 0 && state.LastSeen == Now + 1);
        });
        check("temporary receipt requires signed token timestamps and consumed id", () =>
        {
            var original = Active();
            foreach (Action<GuardState> mutate in new Action<GuardState>[] {
                s => s.TemporaryPass = null, s => s.TemporaryActivatedAt = null, s => s.TemporaryUntil = null,
                s => s.TemporaryUntil = Now + 1801, s => s.UsedPasses.Clear(), s => s.TemporaryPass = "invalid",
                s => s.Committed = false, s => s.Nonce = GuardState.NewNonce() })
            {
                var state = Clone(original); mutate(state); Expect(!Rules.TemporaryActive(state, publicKey, Now));
            }
        });
        check("temporary receipt rejects activation outside signed redemption window", () =>
        {
            var state = Active(); state.TemporaryActivatedAt = Now - 61; state.TemporaryUntil = Now - 61 + 1800;
            Expect(!Rules.TemporaryActive(state, publicKey, Now));
            state.TemporaryActivatedAt = Now + 86400; state.TemporaryUntil = Now + 86400 + 1800;
            Expect(!Rules.TemporaryActive(state, publicKey, Now + 86400));
        });
        check("temporary activation arithmetic overflow does not mutate state", () =>
        {
            var state = Fresh(); var grant = Temporary(state) with { IssuedAt = long.MaxValue - 100, RedeemBefore = long.MaxValue };
            string token = Sign(grant);
            Reject(() => Rules.ActivateTemporary(state, token, publicKey, long.MaxValue - 50));
            Expect(state.TemporaryPass == null && state.TemporaryUntil == null && state.UsedPasses.Count == 0);
        });
        check("saved temporary receipt survives restart without extending its deadline", () =>
        {
            var store = Store("restart"); string token = Sign(Temporary(store.State));
            store.Commit(s => Rules.ActivateTemporary(s, token, publicKey, Now));
            var restarted = new StateStore(store.DirectoryPath);
            Expect(!restarted.Fault && restarted.State.TemporaryPass == token && restarted.State.TemporaryActivatedAt == Now &&
                restarted.State.TemporaryUntil == Now + 1800 && restarted.State.LastSeen == Now &&
                restarted.State.UsedPasses.SequenceEqual(new[] { "temporary-test" }) && Rules.TemporaryActive(restarted.State, publicKey, Now + 1799));
            Reject(() => restarted.Commit(s => Rules.ActivateTemporary(s, token, publicKey, Now + 1800)));
            Expect(restarted.State.TemporaryUntil == Now + 1800 && !Rules.TemporaryActive(restarted.State, publicKey, Now + 1800));
        });
        check("temporary save failure rolls back memory disk and consumption", () =>
        {
            var store = Store("save-failure"); string token = Sign(Temporary(store.State));
            string before = JsonSerializer.Serialize(store.State), blocked = Path.Combine(store.DirectoryPath, "state.json.tmp");
            Directory.CreateDirectory(blocked);
            try { Reject(() => store.Commit(s => Rules.ActivateTemporary(s, token, publicKey, Now))); }
            finally { Directory.Delete(blocked); }
            Expect(JsonSerializer.Serialize(store.State) == before && JsonSerializer.Serialize(new StateStore(store.DirectoryPath).State) == before &&
                !Rules.TemporaryActive(store.State, publicKey, Now));
            store.Commit(s => Rules.ActivateTemporary(s, token, publicKey, Now));
            Expect(Rules.TemporaryActive(store.State, publicKey, Now) && store.State.UsedPasses.Count == 1);
        });
        check("initialization marker failure occurs before temporary state commit", () =>
        {
            var store = Store("marker-failure"); string token = Sign(Temporary(store.State));
            string before = JsonSerializer.Serialize(store.State), marker = Path.Combine(store.DirectoryPath, "initialized");
            File.Delete(marker); Directory.CreateDirectory(marker);
            try { Reject(() => store.Commit(s => Rules.ActivateTemporary(s, token, publicKey, Now))); }
            finally { Directory.Delete(marker); }
            Expect(JsonSerializer.Serialize(store.State) == before && JsonSerializer.Serialize(new StateStore(store.DirectoryPath).State) == before &&
                !Rules.TemporaryActive(store.State, publicKey, Now));
        });
        check("throwing state transaction restores prior temporary policy", () =>
        {
            var store = Store("mutation-failure"); string before = JsonSerializer.Serialize(store.State);
            Reject(() => store.Commit(s => { s.RecoveryFinished = true; s.UsedPasses.Add("uncommitted"); throw new IOException("Fixture mutation failure."); }));
            Expect(JsonSerializer.Serialize(store.State) == before && JsonSerializer.Serialize(new StateStore(store.DirectoryPath).State) == before);
        });
    }

    public static void VerifyInterop(string fixturePath)
    {
        if (new FileInfo(fixturePath).Length > 100000) throw new InvalidDataException("Test credential fixture is too large.");
        var fixture = JsonSerializer.Deserialize<InteropFixture>(File.ReadAllText(fixturePath))
            ?? throw new InvalidDataException("Test credential fixture is invalid.");
        var state = fixture.State;
        var grant = Rules.ValidateTemporary(fixture.Token, fixture.PublicKey, state, fixture.Now, true);
        Expect(grant.UnlockMode == "temporary" && grant.VerifiedPulls == 0 && grant.DurationSeconds == 1800 && !string.IsNullOrWhiteSpace(grant.ApprovalReason));
        Reject(() => Rules.Validate(fixture.Token, fixture.PublicKey, state, fixture.Now, true));
        string approvalReason = grant.ApprovalReason;
        Rules.ActivateTemporary(state, fixture.Token, fixture.PublicKey, fixture.Now);
        Expect(state.TemporaryPass == fixture.Token && state.TemporaryActivatedAt == fixture.Now && state.TemporaryUntil == fixture.Now + 1800 &&
            state.UsedPasses.Count(id => id == grant.Id) == 1 && state.TargetPulls == 600 && state.UnlockMode == "permanent" &&
            !Rules.Ended(state, fixture.PublicKey) && Rules.TemporaryActive(state, fixture.PublicKey, fixture.Now + 1799) &&
            !Rules.TemporaryActive(state, fixture.PublicKey, fixture.Now + 1800) &&
            Rules.ValidateTemporary(fixture.Token, fixture.PublicKey, state, fixture.Now, false).ApprovalReason == approvalReason);
        Reject(() => Rules.ValidateTemporary(fixture.Token, fixture.PublicKey, state, fixture.Now, true));
        Reject(() => Rules.ActivateTemporary(state, fixture.Token, fixture.PublicKey, fixture.Now + 1));
        Expect(state.TemporaryUntil == fixture.Now + 1800 && state.UsedPasses.Count(id => id == grant.Id) == 1);
    }

    sealed record InteropFixture(GuardState State, string PublicKey, string Token, long Now);
}
