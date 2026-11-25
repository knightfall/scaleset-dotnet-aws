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

## Remaining matches in the published alpha.1 baseline

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

### Higher ratings in correlated NVD data

In each published alpha.1 report, Grype matched the Ubuntu package and
recorded the Ubuntu advisory as the primary result. The same report includes
correlated `nvd:cpe` entries. Twenty package/advisory matches carry a
correlated NVD High rating across 11 CVEs even though the Ubuntu ratings
are Medium, Low, or Negligible. The table gives the maximum CVSS base score
recorded in that NVD entry. The automated threshold uses the primary
vulnerability match. Review both sources for the deployed package and feature
before promotion. [Alpha.1 scan assets](https://github.com/knightfall/scaleset-dotnet-aws/releases/tag/v0.1.0-alpha.1)

| Advisory | NVD score | Applicability evidence and disposition for alpha.1 |
| --- | ---: | --- |
| [CVE-2016-20013](https://nvd.nist.gov/vuln/detail/CVE-2016-20013) | 7.5 | Ubuntu recorded `libc6` as Negligible with no planned fix. The costly password-hash path requires SHA-256 or SHA-512 crypt processing. The controller uses token and role credentials and exposes no password-verification endpoint. Track the installed package as residual exposure. |
| [CVE-2025-5222](https://nvd.nist.gov/vuln/detail/CVE-2025-5222) | 7.0 | Ubuntu recorded `libicu74` as Low. The described path is in the ICU `genrb` resource compiler. Neither archive contains `genrb`. Track the package match and reassess if image composition changes. |
| [CVE-2026-107494](https://nvd.nist.gov/vuln/detail/CVE-2026-107494), [CVE-2026-124709](https://nvd.nist.gov/vuln/detail/CVE-2026-124709), [CVE-2026-95691](https://nvd.nist.gov/vuln/detail/CVE-2026-95691) | 7.5, 7.5, 8.1 | Ubuntu recorded both OpenSSL packages as Medium. The described paths require QUIC. Neither archive contains `libmsquic`, and the controller health server listens on HTTP/1. Track the package matches and reassess if QUIC becomes a supported transport. |
| [CVE-2026-107493](https://nvd.nist.gov/vuln/detail/CVE-2026-107493) | 7.7 | Ubuntu recorded both OpenSSL packages as Medium. The PEM path requires a caller-supplied existing stack. No controller source calls that interface directly. Retain the match since the source check does not establish transitive library reachability. |
| [CVE-2026-107496](https://nvd.nist.gov/vuln/detail/CVE-2026-107496), [CVE-2026-121482](https://nvd.nist.gov/vuln/detail/CVE-2026-121482) | 7.5 each | Ubuntu recorded both OpenSSL packages as Medium. The paths require CMS encryption with an attacker-selected recipient certificate or CMS/PKCS#7 signature verification with CRL checking. The controller has no configured CMS processing path. Track package updates and reassess if that changes. |
| [CVE-2026-84786](https://nvd.nist.gov/vuln/detail/CVE-2026-84786) | 7.5 | Ubuntu recorded both OpenSSL packages as Medium. The path requires a DANE-enabled TLS client. The controller configures ordinary TLS certificate validation for its HTTPS clients with no DANE option. Retain the package matches. |
| [CVE-2026-88823](https://nvd.nist.gov/vuln/detail/CVE-2026-88823), [CVE-2026-95692](https://nvd.nist.gov/vuln/detail/CVE-2026-95692) | 7.5 each | Ubuntu recorded both OpenSSL packages as Medium. The findings require AES-GCM-SIV with particular AAD or IV handling. The handoff uses .NET `AesGcm`. Track the installed packages and reassess changes to crypto primitives. |

Release maintainers own these dispositions. Refresh them against both exact
archives for each promotion, whenever the matched package or severity changes,
when an upstream fix or updated base becomes available, and when a controller
or image change affects the listed prerequisites. These historical source and
archive checks describe the examined controller. They do not establish the
reachability of every transitive native path or qualify the next candidate.
