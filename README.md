# BRC CODE-DMG
This mod is a port of BotRandomness' GB emulator, CODE-DMG, into Bomb Rush Cyberfunk. It features new additions, including audio emulation and Game Boy Color support.
Comes with the open-source homebrew game, [Tobu Tobu Girl DX](https://tangramgames.itch.io/tobu-tobu-girl-deluxe)!

![Game Boy Emulator](https://github.com/AbsentmindedGCN/BRC-CODE-DMG/blob/main/Other/gb-emu.jpg?raw=true)

## Features
- Full (though definitely inaccurate) GB experience within BRC
- SGB/GBC Compatibility
- Auto Save and Auto Load, letting you quickly resume games
- Ability to remap controls and add a pixel grid filter

---

## FAQs
### > How Do I Load Other Game ROMs?
1. Boot BRC with the plugin installed to generate the config file. It's named *"transrights.codedmg.cfg"*.
2. Access the config file using the Config editor in r2modman.
3. Under "Change Game Rom" set the RomPath to the game you want to load. For example, *"C:\Users\TransRights\Desktop\ROMs\Pokemon - Crystal Version (USA, Europe) (Rev 1).gbc"*.
4. Hit *Save* in the upper right-hand corner.
5. If you're in-game, close the app, then re-open it. The new game will start.

**TIP:** If you ever need to change controls or settings, close the app on your phone, then reopen it. Each time the app is opened, it will re-read your config file.

### > Where do I get Game ROMs?
There's a lot of great Game Boy and Game Boy Color [Homebrew ROMs on itch.io](https://itch.io/games/tag-gameboy)!

### > What are the Controls?
#### Main Controls
- **D-Pad** = Joystick
- **A Button** = A Button
- **B Button** = B Button
- **Start** = X Button
- **Select** = Y Button

#### Keyboard Controls
- **D-Pad** = WASD Keys
- **A Button** = Period Key
- **B Button** = Comma Key
- **Start** = Enter Key
- **Select** = Right Shift Key

---

## Special Thanks
- [Bot Randomness](https://github.com/BotRandomness), for making his [CODE-DMG emulator](https://github.com/BotRandomness/CODE-DMG) open source, it has been an invaluable starting point
- Tangram Games, for making [Tobu Tobu Girl DX](https://github.com/SimonLarsen/tobutobugirl-dx) free and open source, which is included in the app
- [c-sp](https://github.com/c-sp), for compiling [GB Test Roms](https://github.com/c-sp/game-boy-test-roms) to find and address compatibility issues (also Blargg's Test Roms)
- [Marat Fayzullin](https://fms.komkon.org/), for [Pan Docs](https://gbdev.io/pandocs/Audio.html)
- [Gekkio](https://github.com/Gekkio), for [TCAGBD](https://raw.githubusercontent.com/AntonioND/giibiiadvance/master/docs/TCAGBD.pdf) and the [Mooneye GB Test Suite](https://github.com/Gekkio/mooneye-test-suite)
- [LIJI32](https://github.com/LIJI32), for [SameBoy](https://github.com/LIJI32/SameBoy), which was referenced frequently

---

## Notes
- Link Cable/Multiplayer is currently not supported

---

## MIT License
Copyright (c) Absentminded / Bot Randomness

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
