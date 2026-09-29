# Third-party notices

GlassLink is licensed under the GNU General Public License version 3 or later (see `LICENSE` and the "Licence"
section of the README). It uses, and its releases contain, the following components of others under their own terms.

GlassLink is not affiliated with, endorsed by or supported by Microsoft, Asobo, Fenix Simulations or any other
maker of the simulator or aircraft it works with.

## Included in releases, not in the source repository

### SimConnect client library (`SimConnect.dll`)
Copyright (c) Microsoft Corporation. Part of the Microsoft Flight Simulator 2024 SDK, included unmodified so that
GlassLink can talk to a legally installed copy of Microsoft Flight Simulator. Its use is governed by the Microsoft
Flight Simulator SDK licence terms (<https://docs.flightsimulator.com/msfs2024/html/1_Introduction/SDK_EULA.htm>).
It is not covered by GlassLink's licence. A user may replace it with the one from their own SDK installation.

## Used by the DMC (`GlassLink.exe`)

| Component | Copyright | Licence |
|---|---|---|
| .NET runtime and ASP.NET Core (included in the self-contained release) | .NET Foundation and contributors | MIT (<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>, notices: <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>) |
| Microsoft Edge WebView2 SDK | Microsoft Corporation | BSD 3-clause, text below |
| Microsoft Edge WebView2 Runtime | Microsoft Corporation | part of Windows, not included |
| Vortice.Windows (Vortice.DXGI, Vortice.Direct3D11) | Amer Koleci and contributors | MIT |
| Quamotion.TurboJpegWrapper | Quamotion and contributors | MIT |
| libjpeg-turbo (`turbojpeg.dll`) | D. R. Commander, Viktor Szathmáry, the Independent JPEG Group and contributors | IJG licence, BSD 3-clause and zlib (<https://github.com/libjpeg-turbo/libjpeg-turbo/blob/main/LICENSE.md>). This software is based in part on the work of the Independent JPEG Group. |
| Microsoft Visual C++ runtime (`vcruntime140.dll`, with libjpeg-turbo) | Microsoft Corporation | Visual C++ redistributable terms |
| System.IO.Hashing, System.Drawing.Common | .NET Foundation and contributors | MIT |
| Inter typeface (`web/fonts/inter.woff2`, the status page's font) | The Inter Project Authors | SIL Open Font License 1.1 (`web/fonts/OFL.txt`) |

## Built into the DU firmware

| Component | Copyright | Licence |
|---|---|---|
| ESP-IDF, including FreeRTOS (MIT) and the JPEG, PPA, LCD and USB drivers | Espressif Systems and contributors | Apache 2.0 (<https://github.com/espressif/esp-idf/blob/master/LICENSE>) |
| TinyUSB | Ha Thach (tinyusb.org) and contributors | MIT |
| esp_tinyusb | Espressif Systems | Apache 2.0 |
| esp_lcd_lt8912b | Espressif Systems | Apache 2.0 |
| stb_truetype (`firmware/main/third_party/stb_truetype.h`) | Sean Barrett | MIT or public domain (the header's own text) |
| Inter typeface, cut to three weights (`firmware/main/fonts/`, made by `firmware/tools/make_fonts.py`) | The Inter Project Authors | SIL Open Font License 1.1 (`firmware/main/fonts/OFL.txt`) |

## Used by the measuring tools and the Pi viewer (installed with pip, not distributed)

psutil (BSD 3-clause), NumPy (BSD 3-clause), simplejpeg (BSD 2-clause), websockets (BSD 3-clause), pygame (LGPL 2.1),
Python-SimConnect (AGPL 3.0; only `tools/sim_fps.py` uses it, a separate tool that is never part of a release).

## Licence texts

### MIT License (Vortice.Windows, Quamotion.TurboJpegWrapper, TinyUSB, .NET, System.* packages)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### Microsoft Edge WebView2 SDK (BSD 3-clause)

Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
following conditions are met:

* Redistributions of source code must retain the above copyright notice, this list of conditions and the following
  disclaimer.
* Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
  disclaimer in the documentation and/or other materials provided with the distribution.
* The name of Microsoft Corporation, or the names of its contributors may not be used to endorse or promote products
  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

### Apache License 2.0 (ESP-IDF, esp_tinyusb, esp_lcd_lt8912b)

<https://www.apache.org/licenses/LICENSE-2.0>. The full text ships with each component's source.
