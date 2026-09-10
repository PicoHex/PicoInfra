# Security Policy

## Supported Versions

| Version | Supported          |
|---------|--------------------|
| 0.x     | :white_check_mark: |

## Reporting a Vulnerability

PicoInfra takes security seriously. If you discover a security vulnerability, please do **not** open a public issue.

Instead, report it privately by emailing the maintainers at **security@picohex.dev** or by opening a draft security advisory on GitHub:

1. Go to [https://github.com/PicoHex/PicoInfra/security/advisories/new](https://github.com/PicoHex/PicoInfra/security/advisories/new).
2. Provide a detailed description of the vulnerability.
3. Include steps to reproduce, affected versions, and any potential impact.

We will acknowledge receipt within 48 hours and provide an estimated timeline for a fix.

## Disclosure Policy

We follow a coordinated disclosure process:

1. The maintainers acknowledge and validate the report.
2. A fix is prepared and tested in a private branch.
3. The fix is released in a new version.
4. The vulnerability is publicly disclosed after the fix is available.

We aim to resolve critical issues within 7 days of confirmation.

## Scope

This policy applies to the PicoInfra core libraries (PicoDI, PicoCfg, PicoLog and their abstractions, source generators, and DI integration packages). For vulnerabilities in dependencies, please report to the respective upstream project.
