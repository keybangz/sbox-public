using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace NativeEngine
{
    internal static class ExternalInvoker
    {
        delegate void VoidFn();

        internal static void InvokeExternal(string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            IntPtr fn = IntPtr.Zero;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // RTLD_DEFAULT: -2 on macOS/BSD, IntPtr.Zero on Linux/glibc
                IntPtr RTLD_DEFAULT = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? new IntPtr(-2)
                    : IntPtr.Zero;
                fn = dlsym(RTLD_DEFAULT, name);
                if (fn == IntPtr.Zero)
                {
                    // Try dlopen the interpose library directly as a fallback. This
                    // helps when RTLD_DEFAULT doesn't expose the preloaded .so's
                    // symbols to dlsym from managed code.
                    try
                    {
                        IntPtr h = dlopen("libsbox_init.so", RTLD_NOW);
                        if (h != IntPtr.Zero)
                        {
                            fn = dlsym(h, name);
                            // keep the handle around implicitly (we don't close it)
                        }
                    }
                    catch { }
                }
            }

            if (fn == IntPtr.Zero)
                return;

            try
            {
                var d = Marshal.GetDelegateForFunctionPointer<VoidFn>(fn);
                d();
            }
            catch { }
        }

        internal static IntPtr ResolveSymbol(string name)
        {
            if (string.IsNullOrEmpty(name)) return IntPtr.Zero;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                IntPtr RTLD_DEFAULT = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? new IntPtr(-2)
                    : IntPtr.Zero;
                var p = dlsym(RTLD_DEFAULT, name);
                if (p != IntPtr.Zero) return p;
                try
                {
                    IntPtr h = dlopen("libsbox_init.so", RTLD_NOW);
                    if (h != IntPtr.Zero)
                    {
                        p = dlsym(h, name);
                        if (p != IntPtr.Zero) return p;
                    }
                }
                catch { }
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        [DllImport("libc.so.6", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
        static extern IntPtr dlsym_libc(IntPtr handle, string name);

        [DllImport("libc.so.6", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        static extern IntPtr dlopen_libc(string fileName, int flags);

        // dlopen is used as a fallback to open our interpose library directly.
        const int RTLD_NOW = 2;

        static IntPtr dlsym(IntPtr handle, string name)
        {
            try { return dlsym_libc(handle, name); }
            catch (DllNotFoundException) { }
            return dlsym_libc(handle, name);
        }

        static IntPtr dlopen(string fileName, int flags)
        {
            try { return dlopen_libc(fileName, flags); }
            catch (DllNotFoundException) { }
            return dlopen_libc(fileName, flags);
        }
    }
}
