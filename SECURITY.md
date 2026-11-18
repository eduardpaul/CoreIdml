# Security policy

## Reporting a vulnerability

Please report security problems privately, through GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/eduardpaul/CoreIdml/security/advisories/new) (the **Security** tab of
this repository, then **Report a vulnerability**). Do not open a public issue for them.

Include what you can of: the affected package or program and its version (the npm, NuGet or PyPI package version,
the release you downloaded, or the Claude plugin or Claude Desktop extension version), the platform, a file or steps
that reproduce the problem, and what an attacker could do with it.

You should get a first answer within 7 days. Once the problem is confirmed, the fix ships in a new release and the
advisory is published with credit to you, unless you would rather not be named.

## Supported versions

Only the latest release gets security fixes. CoreIdml is before 1.0, so a fix comes as a new version, not a patch to
an older one.

## Scope

Everything in this repository: the CoreIdml library and its bindings (native, Node, WebAssembly, Python, Dart), the
`coreidml` command line and MCP server, the npm, NuGet and PyPI packages, the Claude plugin (`plugins/coreidml`) and
the Claude Desktop extension (`.mcpb`). Examples of what counts: a crafted `.idml`, `.pdf` or XML file that makes
CoreIdml read or write files outside the paths it was given, run code, or crash or hang it in a way that could be
used against someone; a package that ships something other than what this repository builds.

Bugs in PDFium belong to the [PDFium project](https://pdfium.googlesource.com/pdfium/), and bugs in .NET to
[.NET](https://github.com/dotnet/runtime/security/policy); tell us too if a CoreIdml release ships an affected version.
