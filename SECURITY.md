# Security policy

Please do not report vulnerabilities in public issues. Until a private project security contact is published, contact the repository maintainers privately through the hosting platform.

BlueTusk considers credential disclosure, certificate-validation bypass, protocol desynchronisation, unsafe message-length handling, and cross-tenant pool leakage security-sensitive. Reports should include affected versions, impact, and a minimal reproduction when safe.

After V1 publication, the latest stable patch in the 1.x line is serviced.
The release-prepared 1.0.0 candidate is not published or supported until its
protected publication gate completes. Published package versions are immutable;
security fixes produce a new version and may require rollback or pinning while
that version is prepared.
The repository's current threat model, reviewed controls, accepted boundaries,
and repeatable dependency-audit command are recorded in the
[security review](docs/security.md).
