# Medicine Quick Search Backend

This project contains the local backend used by the MediSearch desktop app.

It accepts a keyword and a configurable list of provider search URLs, then returns normalized search results to the desktop UI.

## Provider URL Format

Use `{keyword}` as the placeholder for the search term.

```text
https://example.com/search?q={keyword}
```

No real provider domains are stored in this repository. Configure provider URLs locally or through deployment-specific configuration.
