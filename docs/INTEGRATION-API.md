# Optional Mod integration API

`BazaarDecorTransmog.BazaarDecorTransmogApi` is a public, read-only surface for optional companion Mods. It does not expose appearance data, preset data, UI objects, or save operations.

## Version 1

```csharp
public const int ApiVersion = 1;
public static bool IsAppearanceEditorActive { get; }
```

`IsAppearanceEditorActive` becomes true when the transition into the appearance editor starts. It remains true while the editor, its preset UI, or its exit confirmation is active. During exit, it becomes false at the black-screen step where Transmog restores the stock decor editor and footer.

Consumers should use a BepInEx soft dependency and discover this type only when the Transmog plugin is present. Cache the reflected property getter instead of looking up the type or member every frame. A missing type, missing member, unsupported API version, or invocation failure means that integration is unavailable; the consumer must continue working independently.

`ApiVersion` is a public constant field. The public type name and the meaning of version 1 are compatibility commitments. Transmog does not reference companion Mods, and removing either DLL leaves the other Mod independent.
