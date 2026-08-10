# Contributing

Contributions are welcome through pull requests and issues.

Before submitting a code change:

1. Keep the widget controller-first and preserve native Xbox Game Bar Back/B behavior.
2. Avoid global input interception, artificial controller debounce, or forced focus movement unless a demonstrated platform issue requires it.
3. Keep RTSS polling visibility-aware; do not add background polling loops for individual features.
4. Keep the RTSS plugin command surface narrow and explicit.
5. Run `python scripts/static_check.py` from the repository root.
6. On Windows, build and smoke-test `Release|x64` before requesting merge for runtime changes.

Do not commit generated packages, build output, PFX files, or private signing material.
