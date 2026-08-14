# og:image template

`og-image.html` is the source for `public/og.png` (1200×630), the Open Graph card
referenced from `src/pages/index.astro` and the Starlight `head` config in
`astro.config.mjs`. It uses the site's design language — teal reference-card cover,
Archivo Narrow / Archivo / Fragment Mono — and loads the fonts from
`../node_modules`, so run `pnpm install` first.

## Regenerating

Serve the `site/` directory (font paths resolve through `node_modules`) and
screenshot the page at exactly 1200×630:

```sh
cd site
pnpm install
npx playwright screenshot --viewport-size=1200,630 \
  "$(python3 -c 'import pathlib; print(pathlib.Path("og-template/og-image.html").resolve().as_uri())")" \
  public/og.png
```

If `file://` URLs are blocked in your browser tooling, serve instead:

```sh
python3 -m http.server 8177 &
npx playwright screenshot --viewport-size=1200,630 \
  http://localhost:8177/og-template/og-image.html public/og.png
```

Update the `REV.` date in the template when regenerating.
