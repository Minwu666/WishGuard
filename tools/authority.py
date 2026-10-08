"""Offline reviewer tools. Never give a signing private key to the guarded client."""
import argparse
import base64
import hashlib
import json
import re
import time
import uuid
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec


def require(condition, message):
    if not condition:
        raise ValueError(message)


def encode(value):
    return base64.urlsafe_b64encode(value).decode("ascii").rstrip("=")


def initialize(private_dir, public_output):
    private_file = private_dir / "issuer-private.pem"
    require(not private_file.exists() and not public_output.exists(),
            "Refusing to replace existing keys. Choose a new deployment directory.")
    key = ec.generate_private_key(ec.SECP256R1())
    private_dir.mkdir(parents=True, exist_ok=True)
    public_output.parent.mkdir(parents=True, exist_ok=True)
    with private_file.open("xb") as file:
        file.write(key.private_bytes(serialization.Encoding.PEM,
                                    serialization.PrivateFormat.PKCS8,
                                    serialization.NoEncryption()))
    public_bytes = key.public_key().public_bytes(serialization.Encoding.DER,
                                                serialization.PublicFormat.SubjectPublicKeyInfo)
    with public_output.open("x", encoding="ascii") as file:
        file.write(base64.b64encode(public_bytes).decode("ascii") + "\n")
    return private_file


def make_grant(request, primogems, intertwined, now):
    require(request.get("Version") == "wishguard-v2", "Unsupported request version.")
    require(request.get("TargetPulls") == 600 and request.get("UnlockMode") == "permanent",
            "This deployment requires 600 pulls and permanent release.")
    install = request.get("InstallId", "")
    nonce = request.get("Nonce", "")
    require(isinstance(install, str) and re.fullmatch(r"[a-f0-9]{32}", install), "Invalid installation ID.")
    require(isinstance(nonce, str) and re.fullmatch(r"[A-F0-9]{48}", nonce), "Invalid request nonce.")
    policy = hashlib.sha256(
        f"wishguard-v2|{install}|600|intertwined+floor(primogems/160)|permanent".encode()
    ).hexdigest().upper()
    require(request.get("PolicyId") == policy, "Policy does not match the installation.")
    require(type(primogems) is int and 0 <= primogems <= 100_000_000, "Invalid primogem count.")
    require(type(intertwined) is int and 0 <= intertwined <= 1_000_000, "Invalid intertwined count.")
    total = intertwined + primogems // 160
    require(total >= 600, "The 600-pull goal has not been reached.")
    require(request.get("Primogems") == primogems and request.get("Intertwined") == intertwined
            and request.get("ClaimedPulls") == total, "Reviewed amounts do not match the request.")
    for field in ("RequestedAt", "ObservedAt"):
        stamp = request.get(field)
        require(type(stamp) is int and now - 86400 <= stamp <= now + 60,
                f"{field} is stale or has an invalid clock value.")
    return {"Version": "wishguard-v2", "UnlockMode": "permanent", "InstallId": install,
            "PolicyId": policy, "Nonce": nonce, "Id": uuid.uuid4().hex, "IssuedAt": now,
            "RedeemBefore": now + 7 * 86400, "VerifiedPulls": total}


def sign(grant, key_file):
    key = serialization.load_pem_private_key(key_file.read_bytes(), password=None)
    require(isinstance(key, ec.EllipticCurvePrivateKey) and isinstance(key.curve, ec.SECP256R1),
            "An ECDSA P-256 key is required.")
    raw = json.dumps(grant, separators=(",", ":")).encode("utf-8")
    # cryptography emits ASN.1 DER, matching the client's Rfc3279DerSequence format.
    signature = key.sign(raw, ec.ECDSA(hashes.SHA256()))
    return {"Payload": encode(raw), "Signature": encode(signature)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    init = commands.add_parser("init", help="Create an independent reviewer keypair.")
    init.add_argument("--private-dir", type=Path, required=True)
    init.add_argument("--public-output", type=Path, required=True)
    issue = commands.add_parser("issue", help="Sign only after manually reviewing current evidence.")
    issue.add_argument("--key", type=Path, required=True)
    issue.add_argument("--request", type=Path, required=True)
    issue.add_argument("--primogems", type=int, required=True)
    issue.add_argument("--intertwined", type=int, required=True)
    issue.add_argument("--evidence", type=Path, required=True)
    issue.add_argument("--reviewed", action="store_true", required=True)
    issue.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "init":
            private_file = initialize(args.private_dir, args.public_output)
            print(f"Public key: {args.public_output}\nKeep private: {private_file}")
            return
        require(args.request.stat().st_size <= 16000, "Request file is too large.")
        require(args.evidence.is_file() and args.evidence.stat().st_size > 0, "Current evidence file is required.")
        request = json.loads(args.request.read_text(encoding="utf-8-sig"))
        require(isinstance(request, dict), "Request must be a JSON object.")
        grant = make_grant(request, args.primogems, args.intertwined, int(time.time()))
        token = sign(grant, args.key)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("x", encoding="utf-8") as file:
            json.dump(token, file, indent=2)
        print(f"Issued {args.output}; import within seven days. Accepted release is permanent.")
    except (ValueError, OSError, TypeError) as error:
        parser.exit(1, f"Cannot proceed: {error}\n")


if __name__ == "__main__":
    main()
