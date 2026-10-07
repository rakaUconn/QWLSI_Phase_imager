"""Generates THIRD-PARTY-NOTICES.txt from the license files of the bundled NuGet packages (read-only on the NuGet cache).
Run:  python tools/gen_notices.py   (writes ../THIRD-PARTY-NOTICES.txt next to the solution)"""
import glob, os, re, sys

NUGET = os.path.join(os.path.expanduser('~'), '.nuget', 'packages')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'THIRD-PARTY-NOTICES.txt')
SCRATCH = os.environ.get('QWLSI_SCRATCH', '')


def pkg(name, *rel):
    for d in sorted(glob.glob(os.path.join(NUGET, name, '*')), reverse=True):
        for r in rel:
            p = os.path.join(d, r)
            if os.path.exists(p):
                return open(p, encoding='utf8', errors='replace').read().strip()
    return None


MIT = """Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."""

libtiff = None
if SCRATCH and os.path.exists(os.path.join(SCRATCH, 'libtiff_license.txt')):
    t = open(os.path.join(SCRATCH, 'libtiff_license.txt'), encoding='utf8', errors='replace').read()
    t = t.replace('&#39;', "'").replace('﻿', '')
    a = t.find('Copyright (c) 2008-2019, Bit Miracle'); b = t.find('Improve this Doc')
    libtiff = t[a:b].strip() if a >= 0 else None
libtiff = libtiff or 'See https://bitmiracle.github.io/libtiff.net/help/articles/license.html (BSD-3-Clause style license, Copyright (c) 2008-2019 Bit Miracle; based in part on LibTIFF, the Independent JPEG Group software and ComponentAce zlib).'

sections = []


def add(title, what, license_text, note=None):
    s = '=' * 78 + '\n' + title + '\n' + '=' * 78 + '\n' + what + '\n'
    if note:
        s += '\nNote: ' + note + '\n'
    s += '\n' + (license_text or '(license text not available at build time - see the project web site)') + '\n'
    sections.append(s)


add('Intel(R) oneAPI Math Kernel Library (oneMKL) - FFT engine',
    'Bundled files: mkl_rt.3.dll, mkl_core.3.dll, mkl_intel_thread.3.dll and the CPU-specific kernels mkl_def/mkl_mc3/mkl_avx2/mkl_avx512/mkl_avx10 (*.3.dll). Distributed unmodified, in binary form.\nSource: https://www.intel.com/content/www/us/en/developer/tools/oneapi/onemkl.html  (NuGet package intelmkl.redist.win-x64)',
    pkg('intelmkl.redist.win-x64', 'license.txt'))
add('Intel(R) OpenMP runtime (libiomp5md.dll, libiompstubs5md.dll, libimalloc.dll)',
    'Bundled as a dependency of oneMKL (threading layer). Distributed unmodified, in binary form.\nSource: NuGet package intelopenmp.redist.win',
    pkg('intelopenmp.redist.win', 'license.txt'))
add('HelixToolkit (HelixToolkit.dll, HelixToolkit.SharpDX.Core.dll, HelixToolkit.SharpDX.Core.Wpf.dll)',
    '3-D viewer. Source: https://github.com/helix-toolkit/helix-toolkit (MIT).',
    pkg('helixtoolkit.sharpdx.core.wpf', 'LICENSE'))
add('SharpDX (SharpDX.dll, SharpDX.DXGI.dll, SharpDX.Direct3D11.dll, SharpDX.Direct3D9.dll, SharpDX.Direct2D1.dll, SharpDX.D3DCompiler.dll, SharpDX.Mathematics.dll)',
    'Direct3D 11 managed wrapper used by the 3-D viewer. Copyright (c) 2010-2016 Alexandre Mutel. Source: http://sharpdx.org/ (MIT).',
    'Copyright (c) 2010-2016 Alexandre Mutel\n\n' + MIT,
    note='the license text on sharpdx.org was unreachable when this file was generated; SharpDX is distributed under the MIT license (as declared in its package metadata) and the standard MIT text is reproduced here.')
add('Cyotek.Drawing.BitmapFont (Cyotek.Drawing.BitmapFont.dll)',
    'Dependency of HelixToolkit (bitmap font text for 3-D labels). Copyright (c) 2012-2020 Cyotek Ltd. Source: https://github.com/cyotek/Cyotek.Drawing.BitmapFont (MIT).',
    'Copyright (c) 2012-2020 Cyotek Ltd.\n\n' + MIT)
add('BitMiracle LibTiff.NET (BitMiracle.LibTiff.NET.dll)',
    'TIFF reading and writing. Copyright (c) 2008-2024 Bit Miracle. Source: https://bitmiracle.com/libtiff/', libtiff)
add('.NET Community Toolkit MVVM (CommunityToolkit.Mvvm.dll)',
    'Copyright (c) .NET Foundation and Contributors. Source: https://github.com/CommunityToolkit/dotnet (MIT).',
    pkg('communitytoolkit.mvvm', 'License.md'))
add('Microsoft .NET 8 runtime, Windows Presentation Foundation, Windows Forms, System.Drawing.Common and related libraries',
    'The self-contained build bundles the .NET 8 runtime and the Windows Desktop libraries (System.*.dll, Presentation*.dll, WindowsBase.dll, System.Windows.Forms.dll, ...). Copyright (c) Microsoft Corporation and the .NET Foundation. Source: https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms (MIT).',
    '(c) Microsoft Corporation. All rights reserved.\n\n' + MIT)

head = """QWLSI single-frame wavefront 1.0.0 - THIRD-PARTY NOTICES
=========================================================
This program bundles the third-party components listed below. Their licenses are reproduced as required.
The program itself reads and writes standard file formats only; no component below is modified.
Not bundled: FFTW (an FFTW/GPL benchmark used during development is NOT part of the release), and the XIMEA camera SDK (the release has no camera support).

"""
open(OUT, 'w', encoding='utf8').write(head + '\n'.join(sections))
print('wrote', os.path.abspath(OUT), os.path.getsize(OUT), 'bytes')
