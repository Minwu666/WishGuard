"""Reviewer checks use only temporary keys, never a deployed signing authority."""
import base64
import hashlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from authority import initialize, main, make_grant, make_temporary_grant, sign


def decode(value):
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


class ReviewerTests(unittest.TestCase):
    def setUp(self):
        self.now = 1900000000
        install = "a" * 32
        self.request = {"Version": "wishguard-v2", "TargetPulls": 600, "UnlockMode": "permanent",
                        "InstallId": install, "Nonce": "B" * 48, "Primogems": 96000, "Intertwined": 0,
                        "ClaimedPulls": 600, "RequestedAt": self.now, "ObservedAt": self.now,
                        "PolicyId": hashlib.sha256(f"wishguard-v2|{install}|600|intertwined+floor(primogems/160)|permanent".encode()).hexdigest().upper()}

    def test_signature_matches_exported_public_key(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            key_file = initialize(root / "private", root / "public.txt")
            grant = make_grant(self.request, 96000, 0, self.now)
            token = sign(grant, key_file)
            public = serialization.load_der_public_key(base64.b64decode((root / "public.txt").read_text()))
            public.verify(decode(token["Signature"]), decode(token["Payload"]), ec.ECDSA(hashes.SHA256()))
            self.assertEqual(json.loads(decode(token["Payload"]))["UnlockMode"], "permanent")
            self.assertEqual(grant["RedeemBefore"] - grant["IssuedAt"], 7 * 86400)
            self.assertEqual(set(grant), {"Version", "UnlockMode", "InstallId", "PolicyId", "Nonce", "Id",
                                         "IssuedAt", "RedeemBefore", "VerifiedPulls"})
            with self.assertRaises(ValueError):
                initialize(root / "private", root / "public.txt")

    def test_below_threshold_and_mismatched_counts_rejected(self):
        with self.assertRaises(ValueError):
            make_grant(self.request, 95999, 0, self.now)
        with self.assertRaises(ValueError):
            make_grant(self.request, 96000, 1, self.now)

    def test_old_or_wrong_policy_rejected(self):
        for changes in ({"Version": "wishguard-v1"}, {"UnlockMode": "temporary"}, {"PolicyId": "wrong"}):
            with self.assertRaises(ValueError):
                make_grant(self.request | changes, 96000, 0, self.now)

    def test_stale_and_future_evidence_rejected(self):
        for changes in ({"ObservedAt": self.now - 86401}, {"RequestedAt": self.now + 61}):
            with self.assertRaises(ValueError):
                make_grant(self.request | changes, 96000, 0, self.now)

    def test_temporary_approval_does_not_require_resources_or_the_goal(self):
        requests = [self.request | {"Primogems": 124, "Intertwined": 0, "ClaimedPulls": 0,
                                    "ObservedAt": self.now - 86401},
                    {key: value for key, value in self.request.items()
                     if key not in {"Primogems", "Intertwined", "ClaimedPulls", "ObservedAt"}}]
        for request in requests:
            with self.subTest(request=request):
                before = request.copy()
                grant = make_temporary_grant(request, "  Reviewer approved this session. \n", self.now)
                self.assertEqual(request, before)
                self.assertEqual(grant["UnlockMode"], "temporary")
                self.assertEqual(grant["ApprovalReason"], "Reviewer approved this session.")
                self.assertEqual(grant["DurationSeconds"], 1800)
                self.assertEqual(grant["VerifiedPulls"], 0)
                self.assertEqual(grant["RedeemBefore"] - grant["IssuedAt"], 86400)
                self.assertEqual(grant["Nonce"], request["Nonce"])
                self.assertEqual(grant["PolicyId"], request["PolicyId"])
                self.assertNotIn("ExpiresAt", grant)  # Activation begins on import, not issuance.
                second = make_temporary_grant(request, "Reviewer approved this session.", self.now)
                self.assertNotEqual(grant["Id"], second["Id"])
                with self.assertRaises(ValueError):
                    make_grant(request, 124, 0, self.now)

    def test_temporary_approval_requires_its_own_bounded_reason(self):
        for reason in (None, True, 123, "", " \t\r\n", "x" * 501, "\U0001f31f" * 251):
            with self.subTest(reason=reason), self.assertRaises(ValueError):
                make_temporary_grant(self.request | {"Note": "A user's note is not reviewer approval."}, reason, self.now)
        self.assertEqual(make_temporary_grant(self.request, "x" * 500, self.now)["ApprovalReason"], "x" * 500)
        self.assertEqual(make_temporary_grant(self.request, "\U0001f31f" * 250, self.now)["ApprovalReason"], "\U0001f31f" * 250)

    def test_temporary_approval_keeps_the_permanent_plan_binding(self):
        for changes in ({"Version": "wishguard-v1"}, {"TargetPulls": 300}, {"UnlockMode": "temporary"},
                        {"PolicyId": "wrong"}, {"InstallId": "invalid"}, {"Nonce": "invalid"}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                make_temporary_grant(self.request | changes, "Reviewed exception.", self.now)

    def test_temporary_request_time_boundaries_and_observation_independence(self):
        for stamp in (self.now - 86400, self.now + 60):
            grant = make_temporary_grant(self.request | {"RequestedAt": stamp, "ObservedAt": "unavailable"},
                                         "Reviewed exception.", self.now)
            self.assertEqual(grant["IssuedAt"], self.now)
        for stamp in (self.now - 86401, self.now + 61, None, True, str(self.now)):
            with self.subTest(stamp=stamp), self.assertRaises(ValueError):
                make_temporary_grant(self.request | {"RequestedAt": stamp}, "Reviewed exception.", self.now)

    def test_temporary_signature_covers_reason_duration_mode_and_deadline(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            key_file = initialize(root / "private", root / "public.txt")
            grant = make_temporary_grant(self.request, "核验人批准本次限时体验。", self.now)
            token = sign(grant, key_file)
            public = serialization.load_der_public_key(base64.b64decode((root / "public.txt").read_text()))
            public.verify(decode(token["Signature"]), decode(token["Payload"]), ec.ECDSA(hashes.SHA256()))
            self.assertEqual(json.loads(decode(token["Payload"])), grant)
            for changes in ({"ApprovalReason": "Changed approval."}, {"DurationSeconds": 1801},
                            {"UnlockMode": "permanent", "VerifiedPulls": 600}, {"RedeemBefore": self.now + 7 * 86400}):
                with self.subTest(changes=changes), self.assertRaises(InvalidSignature):
                    changed = json.dumps(grant | changes, separators=(",", ":")).encode("utf-8")
                    public.verify(decode(token["Signature"]), changed, ec.ECDSA(hashes.SHA256()))

    def run_cli(self, arguments):
        output, error = io.StringIO(), io.StringIO()
        with patch.object(sys, "argv", ["authority.py", *map(str, arguments)]), patch("authority.time.time", return_value=self.now), \
                patch("sys.stdout", output), patch("sys.stderr", error):
            try:
                main()
                code = 0
            except SystemExit as exit:
                code = exit.code
        return code, output.getvalue(), error.getvalue()

    def test_temporary_cli_requires_review_and_reason_and_never_overwrites(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            key = initialize(root / "private", root / "public.txt")
            request = root / "request.json"
            request.write_text(json.dumps({field: value for field, value in self.request.items()
                                          if field not in {"Primogems", "Intertwined", "ClaimedPulls", "ObservedAt"}}), encoding="utf-8")
            output = root / "temporary.wishpass"
            base = ["issue-temporary", "--key", key, "--request", request, "--output", output]
            for extra in (["--reason", "Reviewed exception."], ["--reviewed"], ["--reviewed", "--reason", " "]):
                code, _, _ = self.run_cli(base + extra)
                self.assertNotEqual(code, 0)
                self.assertFalse(output.exists())
            code, message, _ = self.run_cli(base + ["--reviewed", "--reason", " Reviewed exception. "])
            self.assertEqual(code, 0)
            self.assertIn("24 hours", message)
            self.assertIn("30 minutes start on import", message)
            self.assertIn("cannot be paused", message)
            self.assertNotIn("permanent", message)
            original = output.read_bytes()
            token = json.loads(original)
            grant = json.loads(decode(token["Payload"]))
            self.assertEqual(grant["UnlockMode"], "temporary")
            self.assertEqual(grant["ApprovalReason"], "Reviewed exception.")
            code, _, _ = self.run_cli(base + ["--reviewed", "--reason", "Another exception."])
            self.assertNotEqual(code, 0)
            self.assertEqual(output.read_bytes(), original)

    def test_cli_commands_cannot_be_mixed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            key = initialize(root / "private", root / "public.txt")
            request = root / "request.json"; request.write_text(json.dumps(self.request), encoding="utf-8")
            evidence = root / "evidence.bin"; evidence.write_bytes(b"synthetic evidence; no real screenshot")
            output = root / "permanent.wishpass"
            permanent = ["issue", "--key", key, "--request", request, "--output", output,
                         "--primogems", 96000, "--intertwined", 0, "--evidence", evidence, "--reviewed"]
            code, _, _ = self.run_cli(permanent + ["--reason", "Cannot turn this command temporary."])
            self.assertNotEqual(code, 0)
            self.assertFalse(output.exists())
            code, message, _ = self.run_cli(permanent)
            self.assertEqual(code, 0)
            self.assertIn("permanent", message)
            self.assertIn("seven days", message)
            self.assertEqual(json.loads(decode(json.loads(output.read_text())["Payload"]))["UnlockMode"], "permanent")
            temporary_output = root / "temporary.wishpass"
            temporary = ["issue-temporary", "--key", key, "--request", request, "--output", temporary_output,
                         "--reviewed", "--reason", "Reviewed exception."]
            for extra in (["--primogems", 96000], ["--duration-seconds", 3600]):
                code, _, _ = self.run_cli(temporary + extra)
                self.assertNotEqual(code, 0)
                self.assertFalse(temporary_output.exists())


if __name__ == "__main__":
    unittest.main()
