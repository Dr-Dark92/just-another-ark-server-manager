# ARK Asset Attribution

JAASM is an unofficial fan-created community project and is not affiliated with,
endorsed by, or sponsored by Studio Wildcard, Snail Games, Fandom, or the ARK wiki
community.

ARK: Survival Evolved, ARK: Survival Ascended, names, logos, item artwork,
engram artwork, and related game assets are property of their respective owners.

## Bundled catalogue artwork

JAASM's release build may include item and engram icons fetched at build time from
publicly available ARK Fandom wiki file assets:

- https://ark.fandom.com/wiki/
- https://survivetheark.com/index.php?/fan_content_guidelines/

The application itself does not contact Fandom or any wiki at runtime. Assets are
copied into the release under:

- Data/Icons/items/
- Data/Icons/engrams/

The exact source URL resolved for each fetched icon is written into the bundled
catalogue metadata and Data/icon-sync-report.json during the build.

If an asset cannot be resolved, JAASM leaves its icon blank rather than substituting
an unrelated image.
