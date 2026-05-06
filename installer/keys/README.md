# Signing keys

## checksum-private.pem (gitignored)
RSA-2048 private key used by `release.ps1` to sign `checksums.sha256` for every release.

**Never commit this file.** It is in `.gitignore` at repo root.

If lost: generate a new keypair, replace `ChecksumPublicKeyPem` in `src/GlDrive/Services/UpdateChecker.cs`,
and ship a new release. Old clients (with the previous embedded public key) will refuse the new release —
users must reinstall manually.

To regenerate:
```bash
mkdir -p installer/keys
dotnet run --project /tmp/genkey
# (or any equivalent that calls RSA.Create(2048).ExportRSAPrivateKeyPem())
```

## checksum-public.pem (committed)
For reference only. The active public key lives embedded as a constant in `UpdateChecker.cs`.
Keep this file in sync with the embedded constant.

## codesign.pfx (gitignored, optional)
For Authenticode signing of the installer + inner GlDrive.exe. Acquire from a trusted CA
(DigiCert / Sectigo / etc.) for production use, or generate a self-signed cert with
`New-SelfSignedCertificate` for local testing.

`release.ps1` does not yet invoke signtool — add that step when you have a cert.
