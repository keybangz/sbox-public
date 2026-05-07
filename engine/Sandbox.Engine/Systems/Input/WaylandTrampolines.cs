using System;
using System.Runtime.InteropServices;
using NativeEngine;
using Sandbox.Internal;

namespace Sandbox.Engine;

/// <summary>
/// Managed-side trampolines for Wayland input interposition.
/// Registers native callbacks to forward input events to InputRouter handlers.
/// </summary>
internal static class WaylandTrampolines
{
    // Delegates matching native pfn_Engine_* signatures
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Tramp_MouseMotion(float dx, float dy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Tramp_MouseButton(long button, int state, int ikeymods);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Tramp_Key(long scanButtonCode, long keyButtonCode, int state, int repeating, int ikeymods);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Tramp_Text(uint key);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Tramp_MouseWheel(int x, int y, int ikeymods);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Tramp_MousePositionChange(float x, float y, float dx, float dy);

    // Static fields to keep delegates alive
    private static readonly Tramp_MouseMotion _mouseMotion = (dx, dy) => InputRouter.OnMouseMotion(dx, dy);
    private static readonly Tramp_MouseButton _mouseButton = (button, state, ikeymods) => InputRouter.OnMouseButton((ButtonCode)button, state != 0, ikeymods);
    private static readonly Tramp_Key _key = (scanButtonCode, keyButtonCode, state, repeating, ikeymods) => InputRouter.OnKey((ButtonCode)scanButtonCode, (ButtonCode)keyButtonCode, state != 0, repeating != 0, ikeymods);
    private static readonly Tramp_Text _text = (key) => InputRouter.OnText(key);
    private static readonly Tramp_MouseWheel _mouseWheel = (x, y, ikeymods) => InputRouter.OnMouseWheel(x, y, ikeymods);
    private static readonly Tramp_MousePositionChange _mousePositionChange = (x, y, dx, dy) => InputRouter.OnMousePositionChange(x, y, dx, dy);

    // DllImport for direct call
    [DllImport("libsbox_init.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sbox_wayland_register_trampolines")]
    private static extern void sbox_wayland_register_trampolines(IntPtr onMouseMotion, IntPtr onMouseButton, IntPtr onKey, IntPtr onText, IntPtr onMouseWheel, IntPtr onMousePositionChange);

    // Managed delegate type for fallback
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RegisterTrampolinesDelegate(IntPtr onMouseMotion, IntPtr onMouseButton, IntPtr onKey, IntPtr onText, IntPtr onMouseWheel, IntPtr onMousePositionChange);

    private static bool _registered = false;

    /// <summary>
    /// Attempts to register trampolines once. Safe to call repeatedly.
    /// </summary>
    public static void TryRegisterOnce()
    {
        if (_registered) return;

        try
        {
            // Try DllImport first
            IntPtr ptrMouseMotion = Marshal.GetFunctionPointerForDelegate(_mouseMotion);
            IntPtr ptrMouseButton = Marshal.GetFunctionPointerForDelegate(_mouseButton);
            IntPtr ptrKey = Marshal.GetFunctionPointerForDelegate(_key);
            IntPtr ptrText = Marshal.GetFunctionPointerForDelegate(_text);
            IntPtr ptrMouseWheel = Marshal.GetFunctionPointerForDelegate(_mouseWheel);
            IntPtr ptrMousePositionChange = Marshal.GetFunctionPointerForDelegate(_mousePositionChange);

            sbox_wayland_register_trampolines(ptrMouseMotion, ptrMouseButton, ptrKey, ptrText, ptrMouseWheel, ptrMousePositionChange);
            Log.Info("[InputDiag] Wayland trampolines registered via DllImport");
            _registered = true;
        }
        catch (DllNotFoundException)
        {
            // Fallback to ResolveSymbol
            try
            {
                IntPtr registerPtr = NativeEngine.ExternalInvoker.ResolveSymbol("sbox_wayland_register_trampolines");
                if (registerPtr != IntPtr.Zero)
                {
                    Log.Info($"[InputDiag] Resolved sbox_wayland_register_trampolines at {registerPtr}");
                    RegisterTrampolinesDelegate registerDelegate = Marshal.GetDelegateForFunctionPointer<RegisterTrampolinesDelegate>(registerPtr);

                    IntPtr ptrMouseMotion = Marshal.GetFunctionPointerForDelegate(_mouseMotion);
                    IntPtr ptrMouseButton = Marshal.GetFunctionPointerForDelegate(_mouseButton);
                    IntPtr ptrKey = Marshal.GetFunctionPointerForDelegate(_key);
                    IntPtr ptrText = Marshal.GetFunctionPointerForDelegate(_text);
                    IntPtr ptrMouseWheel = Marshal.GetFunctionPointerForDelegate(_mouseWheel);
                    IntPtr ptrMousePositionChange = Marshal.GetFunctionPointerForDelegate(_mousePositionChange);

                    registerDelegate(ptrMouseMotion, ptrMouseButton, ptrKey, ptrText, ptrMouseWheel, ptrMousePositionChange);
                    Log.Info("[InputDiag] Wayland trampolines registered via ResolveSymbol");
                    _registered = true;
                }
                else
                {
                    Log.Warning("[InputDiag] sbox_wayland_register_trampolines symbol not found, trampolines not registered");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[InputDiag] Failed to register Wayland trampolines via ResolveSymbol: {ex.Message}");
            }
        }
        catch (EntryPointNotFoundException)
        {
            Log.Warning("[InputDiag] sbox_wayland_register_trampolines entry point not found, trampolines not registered");
        }
        catch (Exception ex)
        {
            Log.Warning($"[InputDiag] Failed to register Wayland trampolines: {ex.Message}");
        }
    }
}