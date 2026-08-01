# Security Policy

## Supported versions

Security fixes are made for the latest published stable release and, while a release candidate is active, the latest published release candidate. Older prereleases and superseded `0.x` builds do not receive security updates.

## Reporting a vulnerability

Please do not open a public Issue for a suspected vulnerability. Use [GitHub private vulnerability reporting](https://github.com/SunSi12138/SharpLink/security/advisories/new) so the report, discussion, and any temporary patch remain private until coordinated disclosure.

Include the affected package and version, operating system and runtime, a minimal reproduction or proof of concept, the expected impact, and any known mitigations. Do not include secrets or data belonging to other people.

The maintainer will make a best effort to acknowledge a complete report within five business days, validate its severity, and coordinate a disclosure date. A fix may be released sooner when active exploitation or a high-impact remote attack is plausible.

## Scope

SharpLink's protocol parsing, authentication and authorization boundaries, TLS configuration, source generator, serializers, transports, and denial-of-service limits are in scope. Vulnerabilities in third-party dependencies should also be reported privately when they are exploitable through SharpLink.

Reports that require a caller to intentionally disable documented security checks, or that only demonstrate unsupported deployment configurations without a SharpLink boundary failure, may be closed as out of scope.
