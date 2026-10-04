# web-platform-tests data

Files copied unchanged from [web-platform-tests](https://github.com/web-platform-tests/wpt) at
commit `13748c36fdf9dd77cd0b34dd6b244256b365ea60`, under their own license (the 3-Clause BSD
License, in `LICENSE.md` here):

| File | Used by |
| --- | --- |
| `fetch/data-urls/resources/base64.json` | `DataUrlTests` — forgiving-base64 bodies, as WPT's `base64.any.js` applies them (`data:;base64,` + input) |
| `fetch/data-urls/resources/data-urls.json` | `DataUrlTests` — the `data:` URL processor's bodies and the essence of its MIME types (their parameters are not checked) |

To update them, copy the files from a newer commit and change the commit above.
