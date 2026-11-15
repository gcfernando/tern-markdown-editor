# Tern manifest editor schema

These four XSDs describe the elements and settings used by Tern's current
`src/Tern.Desktop/app.manifest`. They are a project-specific validation profile,
not Microsoft's complete Windows manifest schema. Extend this profile when adding
manifest capabilities; do not remove valid application settings to satisfy it.

After opening `Tern.slnx` in Rider, run `python tools/ConfigureRiderSchemas.py`.
It registers the four namespaces in the checkout's ignored `.idea` settings,
preserving unrelated settings. Reopen the solution if Rider has cached its schema
catalog. It does not disable XML inspections or change the application manifest.

The Windows compiler and native startup verification remain necessary. This editor
profile does not replace Windows' own manifest processing.
