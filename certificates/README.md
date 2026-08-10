# Public signing certificate

Do not place a private key or PFX in this directory.

For GitHub sideload releases, generate the signing identity locally with `scripts/create-signing-cert.ps1`. The public `RTSSGameBar-Signing.cer` may be attached to a GitHub Release or copied here if you intentionally want the repository to publish that public certificate.

The matching `RTSSGameBar-Signing.pfx` and its password must remain private and are ignored by Git.
