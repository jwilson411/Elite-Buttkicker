# Font Awesome Free 6.4.0 (vendored)

The pages used to load `all.min.css` from `cdnjs.cloudflare.com`. A domain allowlist in the
Content-Security-Policy is not integrity pinning, so a compromised or intercepted CDN could have
handed the UI any stylesheet and any font it liked. These files are the same release, served from
this build, which is why the CSP in `WebUiConfiguration` no longer names a third-party origin.

Upstream: <https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/>

| File | SHA-384 (Subresource Integrity form) |
| --- | --- |
| `css/fontawesome.min.css` | `sha384-bGIKHDMAvn+yR8S/yTRi+6S++WqBdA+TaJ1nOZf079H6r492oh7V6uAqq739oSZC` |
| `css/solid.min.css` | `sha384-o96F2rFLAgwGpsvjLInkYtEFanaHuHeDtH47SxRhOsBCB2GOvUZke4yVjULPMFnv` |
| `webfonts/fa-solid-900.woff2` | `sha384-JtHMcbwFK+S5WYliXJYzBoASDLTpVrtok44OrbDd8U2VhZIuoYbT6fgtNq8ph8qq` |
| `webfonts/fa-solid-900.ttf` | `sha384-Zr+WfH0OMrd25H7VyB+c7XK9hFubDWH/IM+eMMEIWOt/J0PDnhQWG2dhfrkpA4+n` |

Both stylesheets were checked against the SHA-512 hashes cdnjs publishes for this release before
being committed, and the webfonts against the same files in the upstream `6.4.0` tag.

Only the solid family is here, because every icon the UI asks for is a `fas` class - the core
stylesheet supplies the icon and animation classes, `solid.min.css` the single `@font-face`. Adding a
`far` or `fab` icon to a page means vendoring that family's stylesheet and webfont the same way.

Licence: `LICENSE.txt` (Icons CC BY 4.0, Fonts SIL OFL 1.1, Code MIT), Copyright 2023 Fonticons, Inc.
