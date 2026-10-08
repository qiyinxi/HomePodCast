"""Generate reference vectors for HomePodCast's protocol unit tests.

SRP: srptools (what pyatv uses, and pairs with real HomePods) with fixed secrets.
bplist: Python's plistlib with a mix of types.
Run with the pyatv venv:  C:\\Users\\qiyin\\HomePodCast\\venv\\Scripts\\python.exe gen_vectors.py
"""

import base64
import hashlib
import json
import plistlib
from pathlib import Path

from srptools import SRPClientSession, SRPContext, SRPServerSession, constants

out = {}

# --- SRP-6a, HomeKit parameters (3072-bit, SHA-512, user Pair-Setup, PIN 3939)
ctx = SRPContext("Pair-Setup", "3939", prime=constants.PRIME_3072,
                 generator=constants.PRIME_3072_GEN, hash_func=hashlib.sha512)
cases = []
for i, (a_hex, b_hex, salt_hex) in enumerate([
    ("a" * 64, "b" * 64, "0102030405060708090a0b0c0d0e0f10"),
    ("0123456789abcdef" * 4, "fedcba9876543210" * 4, "00112233445566778899aabbccddeeff"),
    ("1" + "0" * 63, "2" + "0" * 63, "ffeeddccbbaa99887766554433221100"),
]):
    salt = bytes.fromhex(salt_hex)
    verifier = ctx.get_common_password_verifier(ctx.get_common_password_hash(salt))
    server = SRPServerSession(ctx, "%x" % verifier, private=b_hex)
    client = SRPClientSession(ctx, private=a_hex)
    client.process(server.public, salt_hex)
    server.process(client.public, salt_hex)
    assert client.key == server.key, "client/server disagree"
    text = lambda v: v.decode() if isinstance(v, bytes) else v  # srptools returns hexlified bytes
    cases.append({
        "clientPrivate": a_hex,
        "salt": salt_hex,
        "serverPublic": text(server.public),
        "clientPublic": text(client.public),
        "sessionKey": text(client.key),
        "proof": text(client.key_proof),
    })
out["srp"] = cases

# --- binary plist produced by Apple's format implementation in Python
sample = {
    "name": "卧室",
    "ascii": "HomePod",
    "small": 7,
    "u16": 300,
    "u32": 70000,
    "big": 2**40,
    "neg": -5,
    "real": 1.5,
    "yes": True,
    "no": False,
    "data": bytes(range(20)),
    "list": [1, "two", {"three": 3}],
    "streams": [{"type": 96, "dataPort": 50683, "controlPort": 55975}],
}
out["bplist"] = {
    "b64": base64.b64encode(plistlib.dumps(sample, fmt=plistlib.FMT_BINARY)).decode(),
}

Path(__file__).with_name("vectors.json").write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")
print("wrote", len(cases), "SRP cases and a bplist sample")
