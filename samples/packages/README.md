# Demos of the published packages

Each folder fills the same template with a published CoreIdml package, installed from its registry the way an
application would: the plugin's data sheet template and its data (`plugins/coreidml/samples`). Each demo fills the
template, validates it, saves it, opens the saved file again, exports a PDF and imports that PDF back. Each one prints
a line per check and exits with 1 when one fails. The output goes to `out/` in its folder.

| Folder | Package | Run |
| --- | --- | --- |
| `python` | [`coreidml`](https://pypi.org/project/coreidml/) from PyPI | `pip install -r requirements.txt` then `python demo.py` |
| `dotnet` | [`CoreIdml.Pdf`](https://www.nuget.org/packages/CoreIdml.Pdf) from nuget.org | `dotnet run` |
| `node` | [`@coreidml/node`](https://www.npmjs.com/package/@coreidml/node) from npm | `npm install` then `node demo.mjs` |
| `web` | [`@coreidml/wasm`](https://www.npmjs.com/package/@coreidml/wasm) from npm, in a Web Worker | `npm install` then `node serve.mjs` and open the address it prints |

`node serve.mjs --check` runs the web demo in headless Chrome, Edge or Chromium instead, and exits with its result.
The page brings its own fonts (Liberation Sans, from `tests/fixtures/fonts`), since a browser has no system fonts
to give it.

The versions are the last release. The release workflow moves them along with every other version once a release is
out (`eng/set-version.mjs`), and the **Published packages** workflow (`.github/workflows/packages.yml`) then runs all
four demos on Windows, macOS and Linux, x64 and arm64, with the packages it has just published. Run it by hand
(Actions, Published packages, Run workflow) to check any published version.

The folder has its own `Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props`, so that
none of the repository's build settings reach the .NET demo: it builds as any application that references the
package would.
