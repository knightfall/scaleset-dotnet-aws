# Controller image advisory record

The controller uses the Microsoft .NET 10.0.15 Ubuntu 24.04 chiseled-extra
multi-architecture base at
`sha256:c8dd08a50d8ffd19bd8da72edee05a7ac15b486a5b1869cf46fdb370bf1fbc76`.
The two architecture-specific base digests are asserted by the
[OCI verifier](../../tests/oci/verify_controller_image.py). The release
workflow runs a scan against each tested archive and seals the reports with
the archives, SBOMs, and checksums. The exact build's `release.json` identifies
the scanner, database, archive, and OCI manifest for each architecture.
[Microsoft .NET servicing policy](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)

## Review baseline comparison

The initial patched builds on 25 November 2026 used Grype `0.122.1` and
database schema `v6.1.10`, built `2026-11-24T06:22:05Z`. The same database
was used for the original review. Both architectures ran the packaged health
suite with 11 passes. The arm64 test ran natively on the local ARM Linux
engine. The amd64 local test used CPU emulation.

| Package/advisory matches | Original x64 | Patched x64 | Original arm64 | Patched arm64 |
| --- | ---: | ---: | ---: | ---: |
| Critical | 2 | 0 | 2 | 0 |
| High | 15 | 0 | 15 | 0 |
| Medium | 51 | 31 | 51 | 31 |
| Low | 17 | 1 | 17 | 1 |
| Negligible | 1 | 1 | 1 | 1 |
| Total | 86 | 33 | 86 | 33 |

Comparing by package and advisory ID, each patched archive removed all 53
matches that had a listed fix. The 33 remaining matches were already present
in the reviewed archive. There were zero new matches and no severity
exceptions. [Reviewed project revision](https://github.com/knightfall/scaleset-dotnet-aws/commit/35f4c484084e74d206d311affc311a593c0d1583)

| Initial local build | Archive SHA-256 | OCI manifest SHA-256 |
| --- | --- | --- |
| Linux amd64 | `64a994854e36dc94425660c9185e629b6e05574a6efe61a66b5d94ce65d53d73` | `5aec0b3404d9e1cebfa72d5d9b020f0b54e62c7c224632ab909fdf9365db837d` |
| Linux arm64 | `5968458ebfd5890ff0eae74e08b03de47e8ba53c39587d078923697e632ddf9f` | `a418bdd4d6bfc4470fc0827aaa6c8338648acffb90e074d3dd7673c006cf9990` |

## Remaining matches

The package/advisory sets below apply to both architectures. Release
maintainers recheck them when Ubuntu publishes a fix or revises applicability,
when Microsoft refreshes the base image, and when a new candidate scan changes
the matches. The hosted release workflow gates each fresh scan on severity.

| Package | Severity and Ubuntu status | Advisory IDs |
| --- | --- | --- |
| `libc6` | Medium, no fix listed | `CVE-2026-100290`, `CVE-2026-115819`, `CVE-2026-121757`, `CVE-2026-121758`, `CVE-2026-124024` |
| `libc6` | Negligible, vendor will not fix | [CVE-2016-20013](https://ubuntu.com/security/CVE-2016-20013) |
| `libicu74` | Low, no fix listed | [CVE-2025-5222](https://ubuntu.com/security/CVE-2025-5222) |
| `libssl3t64`, `openssl` | Medium, no fix listed, 13 matches per package | `CVE-2026-107493`, `CVE-2026-107494`, `CVE-2026-107496`, `CVE-2026-121481`, `CVE-2026-121482`, `CVE-2026-124709`, `CVE-2026-35190`, `CVE-2026-84786`, `CVE-2026-88822`, `CVE-2026-88823`, `CVE-2026-95690`, `CVE-2026-95691`, `CVE-2026-95692` |

The per-image Grype reports retain installed versions, fix status, and source
links. Check the Ubuntu advisory for any changed status. A scanner error,
missing architecture, invalid database, or unresolved High or Critical match
blocks promotion. [Ubuntu security notices](https://ubuntu.com/security/notices) · [Grype database freshness](https://oss.anchore.com/docs/guides/vulnerability/database/)
