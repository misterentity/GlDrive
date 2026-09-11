"""Disposable loopback FTPS fixture. See README.md; never serves application data."""
import datetime
import json
import pathlib
import sys
import tempfile

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import TLS_FTPHandler
from pyftpdlib.servers import FTPServer

root = pathlib.Path(tempfile.mkdtemp(prefix="gldrive-ftps-check-"))
data = root / "data"
data.mkdir()
key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
now = datetime.datetime.now(datetime.timezone.utc)
cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
        .public_key(key.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(minutes=1))
        .not_valid_after(now + datetime.timedelta(days=1)).sign(key, hashes.SHA256()))
pem = root / "fixture.pem"
pem.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                 serialization.NoEncryption()) + cert.public_bytes(serialization.Encoding.PEM))
class ReleaseCheckHandler(TLS_FTPHandler):
    def ftp_NOOP(self, line):
        reject = root / "reject-noop-once"
        if reject.exists():
            remaining = int(reject.read_text()) - 1
            if remaining > 0:
                reject.write_text(str(remaining))
            else:
                reject.unlink()
            with (root / "noop-replies").open("a") as replies:
                replies.write("500\n")
            self.respond("500 Fixture NOOP rejection")
        else:
            with (root / "noop-replies").open("a") as replies:
                replies.write("200\n")
            super().ftp_NOOP(line)


authorizer = DummyAuthorizer()
authorizer.add_user("release-check", "", str(data), perm="elradfmwMT")
ReleaseCheckHandler.authorizer = authorizer
ReleaseCheckHandler.certfile = str(pem)
ReleaseCheckHandler.tls_control_required = True
ReleaseCheckHandler.tls_data_required = True
server = FTPServer(("127.0.0.1", 0), ReleaseCheckHandler)
state = {"port": server.socket.getsockname()[1], "root": str(root),
         "fingerprint": cert.fingerprint(hashes.SHA256()).hex().upper()}
pathlib.Path(sys.argv[1]).write_text(json.dumps(state))
print("Disposable FTPS fixture ready", flush=True)
server.serve_forever()
