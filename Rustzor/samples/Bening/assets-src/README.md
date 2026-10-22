# Bening's 3D model

`make_ipa.py` builds `../wwwroot/models/ipa.glb`, the plant IPA Leuwi Bening, in Blender 5.x:

```python
exec(open(r"<repo>/Rustzor/samples/Bening/assets-src/make_ipa.py").read())
```

The script works in its own scene and removes it afterwards. See the header of the script for
the custom properties (`asset`, `spin`, `water`, `level`) the engine reads.
