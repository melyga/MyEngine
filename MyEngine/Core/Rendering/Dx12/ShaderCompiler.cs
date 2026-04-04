#nullable enable

using Silk.NET.Core.Native;
using System.Runtime.InteropServices;
using System.Text;

namespace MyEngine.Core.Rendering.Dx12;

/// <summary>
/// Компилирует HLSL-шейдеры в DXIL-блобы через IDxcCompiler3.
/// Результаты кэшируются по (hlslPath, entryPoint): повторный вызов
/// с теми же аргументами возвращает уже скомпилированный блоб.
/// </summary>
internal sealed unsafe class ShaderCompiler : IDisposable
{
    // ── CLSID ─────────────────────────────────────────────────────────────────────
    // CLSID_DxcCompiler  {73e22d93-e6ce-47f3-b5bf-f0664f39c1b0}
    // CLSID_DxcUtils     {6245d6af-66e0-48fd-80b4-4d271796748c}
    private static readonly Guid ClsidDxcCompiler = new("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0");
    private static readonly Guid ClsidDxcUtils    = new("6245d6af-66e0-48fd-80b4-4d271796748c");

    // DXC_CP_UTF8 = 65001
    private const uint DxcCpUtf8 = 65_001u;

    // ── API и COM-объекты ─────────────────────────────────────────────────────────

    private readonly Dxc _api;
    private ComPtr<IDxcCompiler3>      _compiler;
    private ComPtr<IDxcUtils>          _utils;
    private ComPtr<IDxcIncludeHandler> _includeHandler;

    // ── Кэш ──────────────────────────────────────────────────────────────────────

    private readonly Dictionary<(string path, string entry), ComPtr<IDxcBlob>> _cache = new();
    private bool _disposed;

    // ── Конструктор ───────────────────────────────────────────────────────────────

    public ShaderCompiler()
    {
        _api = Dxc.GetApi();

        void* ptr;
        Guid  iid;

        // IDxcCompiler3
        ptr = null;
        iid = IDxcCompiler3.Guid;
        SilkMarshal.ThrowHResult(
            _api.DxcCreateInstance(ref ClsidDxcCompiler, ref iid, ref ptr));
        _compiler = new ComPtr<IDxcCompiler3>((IDxcCompiler3*)ptr);

        // IDxcUtils (создание source-блобов, include handler)
        ptr = null;
        iid = IDxcUtils.Guid;
        SilkMarshal.ThrowHResult(
            _api.DxcCreateInstance(ref ClsidDxcUtils, ref iid, ref ptr));
        _utils = new ComPtr<IDxcUtils>((IDxcUtils*)ptr);

        // Include handler по умолчанию (обрабатывает #include относительно рабочей директории)
        IDxcIncludeHandler* handler = null;
        SilkMarshal.ThrowHResult(
            _utils.Handle->CreateDefaultIncludeHandler(ref handler));
        _includeHandler = new ComPtr<IDxcIncludeHandler>(handler);
    }

    // ── Публичный API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Компилирует HLSL-файл в DXIL-блоб.
    /// </summary>
    /// <param name="hlslPath">Путь к .hlsl-файлу (UTF-8).</param>
    /// <param name="entryPoint">Имя точки входа, например "VSMain".</param>
    /// <param name="profile">
    ///   Целевой профиль: "vs_6_0", "ps_6_0", "cs_6_0" и т.д.
    /// </param>
    /// <returns>
    ///   Указатель на IDxcBlob с DXIL-байткодом.
    ///   Время жизни блоба совпадает с временем жизни ShaderCompiler.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///   Если компиляция завершилась с ошибкой — сообщение содержит текст
    ///   ошибки из IDxcResult.
    /// </exception>
    public IDxcBlob* Compile(string hlslPath, string entryPoint, string profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cacheKey = (hlslPath, entryPoint);
        if (_cache.TryGetValue(cacheKey, out ComPtr<IDxcBlob> cached))
            return cached.Handle;

        // Читаем файл как сырые байты — DXC сам применит Encoding
        byte[] sourceBytes = File.ReadAllBytes(hlslPath);

        // Аргументы DXC (UTF-16 wide strings, как ожидает IDxcCompiler3::Compile)
        string[] argStrings =
        [
            "-E", entryPoint,           // точка входа
            "-T", profile,              // target profile
            "-Zpr",                     // row-major matrices
            "-HV", "2021",              // HLSL 2021
            "-Zi",                      // debug info (PDB-совместимый)
            "-Od",                      // отключить оптимизации для отладки
        ];

        nint[] argHandles = new nint[argStrings.Length];
        try
        {
            // Маршалим каждый аргумент в нативную UTF-16 строку
            for (int i = 0; i < argStrings.Length; i++)
                argHandles[i] = Marshal.StringToHGlobalUni(argStrings[i]);

            fixed (byte*  pSrc  = sourceBytes)
            fixed (nint*  pArgs = argHandles)
            {
                DxcBuffer source = new()
                {
                    Ptr      = pSrc,
                    Size     = (nuint)sourceBytes.Length,
                    Encoding = DxcCpUtf8,
                };

                void* resultPtr = null;
                Guid  resultIid = IDxcResult.Guid;

                SilkMarshal.ThrowHResult(
                    _compiler.Handle->Compile(
                        &source,
                        (char**)pArgs,
                        (uint)argStrings.Length,
                        _includeHandler.Handle,
                        ref resultIid,
                        ref resultPtr));

                IDxcResult* result = (IDxcResult*)resultPtr;
                try
                {
                    return FinalizeCompilation(result, hlslPath, entryPoint, cacheKey);
                }
                finally
                {
                    result->Release();
                }
            }
        }
        finally
        {
            // Освобождаем нативные строки аргументов в любом случае
            foreach (nint h in argHandles)
                if (h != nint.Zero)
                    Marshal.FreeHGlobal(h);
        }
    }

    // ── Приватные вспомогательные методы ─────────────────────────────────────────

    /// <summary>
    /// Проверяет статус компиляции, извлекает DXIL-блоб и кладёт его в кэш.
    /// </summary>
    private IDxcBlob* FinalizeCompilation(
        IDxcResult*      result,
        string           hlslPath,
        string           entryPoint,
        (string, string) cacheKey)
    {
        int compileStatus = 0;
        SilkMarshal.ThrowHResult(result->GetStatus(&compileStatus));

        if (compileStatus != 0)
        {
            string errorText = ExtractErrorText(result);
            throw new InvalidOperationException(
                $"Shader compilation failed [{Path.GetFileName(hlslPath)}::{entryPoint}]:\n{errorText}");
        }

        IDxcBlob* blobPtr = null;
        SilkMarshal.ThrowHResult(result->GetResult(ref blobPtr));

        if (blobPtr == null)
            throw new InvalidOperationException(
                $"IDxcResult.GetResult returned null for [{hlslPath}::{entryPoint}].");

        var comBlob = new ComPtr<IDxcBlob>(blobPtr);
        _cache[cacheKey] = comBlob;
        return comBlob.Handle;
    }

    /// <summary>
    /// Читает текст ошибки из IDxcResult через GetErrorBuffer().
    /// IDxcBlobEncoding наследует IDxcBlob → GetBufferPointer / GetBufferSize.
    /// </summary>
    private static string ExtractErrorText(IDxcResult* result)
    {
        IDxcBlobEncoding* errorBlob = null;
        int hr = result->GetErrorBuffer(ref errorBlob);

        if (hr < 0 || errorBlob == null)
            return "(no error output available from IDxcResult)";

        try
        {
            void*  ptr  = errorBlob->GetBufferPointer();
            nuint  size = errorBlob->GetBufferSize();

            if (ptr == null || size == 0)
                return "(IDxcResult error buffer is empty)";

            // DXC возвращает ошибки в UTF-8
            return Encoding.UTF8.GetString(
                new ReadOnlySpan<byte>((byte*)ptr, (int)size)).TrimEnd('\0');
        }
        finally
        {
            errorBlob->Release();
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Освобождаем все кэшированные DXIL-блобы
        foreach (ComPtr<IDxcBlob> blob in _cache.Values)
            blob.Dispose();
        _cache.Clear();

        // Освобождаем IDxcIncludeHandler, IDxcUtils, IDxcCompiler3
        _includeHandler.Dispose();
        _utils.Dispose();
        _compiler.Dispose();

        // Освобождаем нативный Dxc API-объект (выгружает dxcompiler.dll)
        _api.Dispose();
    }
}

// Назначение:   Компиляция HLSL → DXIL через IDxcCompiler3 с кэшированием блобов
// Зависит от:   Silk.NET.Direct3D.Dxc, Silk.NET.Core.Native, System.IO, System.Text
// Используется: Dx12Renderer (передаёт блобы в D3D12_SHADER_BYTECODE при создании PSO)
