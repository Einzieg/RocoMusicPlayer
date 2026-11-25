# Third-party components

Original RocoPlayer (Xianxu) code is copyright © 2026 Einzieg and distributed under the MIT license. The components below retain their own licenses and notices.

## Spotify Basic Pitch

Copyright 2022 Spotify AB. Apache License 2.0. The ONNX model and portions of the onset decoding and time alignment in `src/RocoPlayer/Audio/` derive from Spotify Basic Pitch. The C# implementation uses ONNX Runtime on CPU, WDL resampling, and a pitch-local acoustic envelope to track the lead voice. The original model, Apache license, upstream commit, hashes and provenance are in `src/RocoPlayer/Assets/models/basic-pitch/`, distributed as `models/basic-pitch/`. No endorsement by Spotify is implied. [Upstream project and sources](https://github.com/spotify/basic-pitch/tree/fa5997af0a8210982619003269994a1be25eddf3) · [Upstream license](https://github.com/spotify/basic-pitch/blob/fa5997af0a8210982619003269994a1be25eddf3/LICENSE)

## Desktop runtime and libraries

The self-contained Windows x64 distribution includes the .NET and Windows Desktop runtimes from Microsoft. Its runtime license and third-party notices are in `licenses/dotnet/`. The player’s direct NuGet package versions are recorded in `src/RocoPlayer/RocoPlayer.csproj`.

| Component | Attribution and accompanying notices |
| --- | --- |
| Microsoft ONNX Runtime | Copyright © Microsoft Corporation. MIT license and third-party notices in `licenses/ONNXRuntime/`. |
| NAudio and its audio modules | Mark Heath and contributors. Copyright 2020 Mark Heath. MIT license in `licenses/NAudio.txt`. |
| NAudio.Vorbis | Copyright © Andrew Ward 2021. MIT license. |
| NVorbis | Copyright © Andrew Ward. MIT license and copyright notice in `licenses/NVorbis.txt`. |
| Html Agility Pack | Copyright © ZZZ Projects Inc. MIT license. Package authors include ZZZ Projects, Simon Mourrier, Jeff Klawiter and Stephan Grell. |

The WDL resampler in NAudio is based on work by Cockos Incorporated, copyright 2005 and later, ported to C# by Mark Heath. [Original WDL notice in the NAudio source](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Core/Dsp/WdlResampler.cs)

For NAudio.Vorbis and Html Agility Pack, retain the copyright notices above together with the MIT permission and warranty terms included in `licenses/LICENSE` in the distribution. Upstream projects include [NAudio](https://github.com/naudio/NAudio), [NAudio.Vorbis](https://github.com/naudio/Vorbis), [NVorbis](https://github.com/NVorbis/NVorbis), [Html Agility Pack](https://github.com/zzzprojects/html-agility-pack) and [ONNX Runtime](https://github.com/microsoft/onnxruntime).

## DD virtual input

The Windows installer includes the original 63340 HID DLL, installer, INF, CAT and SYS from DD. These vendor components remain subject to their own distribution terms. The build verifies the pinned archive and component hashes. The installer checks the service and devices before invoking the vendor's `ddc.exe` on systems missing the driver. The vendor describes online authentication when loading its free edition. [Pinned DD vendor package](https://github.com/ddxoft/master/tree/34a1608ac3754674eb4d2487dc8859b1523bd290) · [DD vendor site](https://www.ddxoft.com/)

The installer also includes Microsoft x64 Visual C++ runtime DLLs next to the vendor installer. These binaries are copied unchanged from the build machine’s system directory after signature and architecture verification. Microsoft’s applicable redistribution terms govern them.

## Music and scores

The score preview embeds RocoMusic's v2 handpan bank, recorded on 2026-09-20. The nine unmodified WAV files are six-second stereo PCM recordings at 24 kHz, distributed in the player assembly. The source bank and its key-to-pitch mapping are used by the RocoMusic score player. [RocoMusic workspace](https://rocomusic.cn/convert) · [C4 recording](https://rocomusic.cn/audio/handpan/v2/T.wav)

Copyright in recordings and scores remains with their respective rightsholders. Online scores are retrieved on user request from [RocoMusic](https://rocomusic.cn/). Personal libraries, recordings and downloaded scores are stored in the user’s data locations.

## Preview audio dynamics

The native stereo compressor uses the Chromium Web Audio compressor algorithm and the sndfilter implementation as references, with the settings used by RocoMusic. [Chromium implementation](https://chromium.googlesource.com/chromium/src/+/main/third_party/blink/renderer/platform/audio/dynamics_compressor.cc) · [sndfilter implementation](https://github.com/velipso/sndfilter/blob/master/src/compressor.c)

```text
Copyright (C) 2011 Google Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.
3. Neither the name of Apple Computer, Inc. ("Apple") nor the names of
   its contributors may be used to endorse or promote products derived
   from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY APPLE AND ITS CONTRIBUTORS "AS IS" AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL APPLE OR ITS CONTRIBUTORS BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

BSD Zero Clause License
Copyright (c) 2024 by Sean Connelly (https://sean.fun)

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
```
