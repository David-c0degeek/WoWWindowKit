# Logo

`logo.png` is the original transparent logo used in the project README.
Generated with the built-in image generation tool. The main pane and four
companion panes represent the utility's five-client layout.

`logo.ico` contains 16, 24, 32, 48, 64, 128 and 256 pixel versions of that logo,
centered on a transparent square. It is embedded in the Windows executable and
used by the shortcuts, control window, Settings and tray icon. To regenerate it
with Python and Pillow:

```powershell
python -c "from PIL import Image, ImageOps; im=Image.open('docs/assets/logo.png').convert('RGBA'); im=ImageOps.pad(im, (1312,1312), color=(0,0,0,0)); im.save('docs/assets/logo.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])"
```

## Generation prompt

```text
Use case: logo-brand
Asset type: GitHub README logo for WoWWindowKit, a Windows utility arranging World of Warcraft client windows into a main viewport and smaller companion windows.
Primary request: Create one polished original emblem, no text. A stylized luminous blue main window pane with four smaller window panes stacked along its right edge, framed in elegant brushed antique gold with subtle fantasy bevels. The arrangement should read instantly as a window layout tool, with an understated arcane aesthetic. Clean silhouette and strong negative space, suitable for a small software project logo. Premium restrained game utility identity, crisp geometry, subtle material depth; not a busy illustration.
Composition: single centered square emblem, generous padding, transparent background. Main pane occupies left two-thirds, four companions in right third. Blue glass, gold trim, restrained cyan highlight. No letters, no words, no watermark, no shields, no official Warcraft marks, no desktop scene. Actual transparency around the emblem.
```
