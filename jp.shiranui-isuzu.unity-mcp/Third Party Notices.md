# Third Party Notices

This package is distributed under the MIT License and redistributes the assemblies below,
which ship in `Editor/Plugins/`.

The `execute_code` tool compiles C# inside the Editor at runtime. Unity does not expose a
compiler for that, so Roslyn is bundled.

## .NET Compiler Platform ("Roslyn") 3.7.0

- `Microsoft.CodeAnalysis.dll`
- `Microsoft.CodeAnalysis.CSharp.dll`

Copyright (c) .NET Foundation and Contributors
<https://github.com/dotnet/roslyn> — MIT License

## .NET Runtime libraries

- `System.Collections.Immutable.dll` (4.6.x, `release/2.1`)
- `System.Reflection.Metadata.dll` (4.6.x, `release/2.1`)
- `System.Runtime.CompilerServices.Unsafe.dll` (3.1.0)

Copyright (c) .NET Foundation and Contributors
<https://github.com/dotnet/runtime> — MIT License

Required by Roslyn rather than used directly.

## Package dependency, not redistributed

`com.unity.nuget.newtonsoft-json` 3.2.1 is resolved by the Package Manager and is not
included here. Json.NET is MIT, © James Newton-King.

## MIT License

Applies to every redistributed component listed above.

```
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
```
