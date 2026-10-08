"""Reviewer checks use only temporary keys, never a deployed signing authority."""
import base64
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from authority import initialize, make_grant, sign


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
            decode = lambda value: base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))
            public.verify(decode(token["Signature"]), decode(token["Payload"]), ec.ECDSA(hashes.SHA256()))
            self.assertEqual(json.loads(decode(token["Payload"]))["UnlockMode"], "permanent")
            self.assertEqual(grant["RedeemBefore"] - grant["IssuedAt"], 7 * 86400)
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


if __name__ == "__main__":
    unittest.main()
