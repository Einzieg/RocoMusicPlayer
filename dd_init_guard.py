"""Limit Windows-key suppression to DD DLL startup, never normal playback."""

from contextlib import contextmanager
import ctypes
from ctypes import wintypes
import threading
import time

from dd_diagnostics import record


SETTLE_SECONDS = 0.52


class _WinKeyHook:
    def __init__(self):
        self.ready = threading.Event()
        self.cancelled = threading.Event()
        self.error = None
        self.thread_id = None
        self.suppressed = 0
        self.thread = threading.Thread(target=self._run, name='dd-init-guard', daemon=True)

    def _filter(self, code, message, data):
        if code >= 0 and ctypes.cast(data, ctypes.POINTER(wintypes.DWORD))[0] in (0x5B, 0x5C):
            # Keep the low-level hook fast: aggregate here, write logs after cleanup.
            self.suppressed += 1
            return 1
        return self.user32.CallNextHookEx(None, code, message, data)

    def _run(self):
        hook = None
        try:
            self.user32 = ctypes.WinDLL('user32', use_last_error=True)
            kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
            callback_type = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)
            api = self.user32
            for name, args, result in (
                ('SetWindowsHookExW', (ctypes.c_int, callback_type, wintypes.HINSTANCE, wintypes.DWORD), wintypes.HANDLE),
                ('CallNextHookEx', (wintypes.HANDLE, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM), ctypes.c_ssize_t),
                ('UnhookWindowsHookEx', (wintypes.HANDLE,), wintypes.BOOL),
                ('GetMessageW', (ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT), ctypes.c_int),
                ('PeekMessageW', (ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT, wintypes.UINT), wintypes.BOOL),
                ('PostThreadMessageW', (wintypes.DWORD, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM), wintypes.BOOL),
                ('GetAsyncKeyState', (ctypes.c_int,), ctypes.c_short),
            ):
                function = getattr(api, name)
                function.argtypes, function.restype = args, result
            kernel32.GetCurrentThreadId.restype = wintypes.DWORD
            kernel32.GetModuleHandleW.argtypes = (wintypes.LPCWSTR,)
            kernel32.GetModuleHandleW.restype = wintypes.HMODULE
            message = wintypes.MSG()
            api.PeekMessageW(ctypes.byref(message), None, 0, 0, 0)
            self.thread_id = kernel32.GetCurrentThreadId()
            callback = callback_type(self._filter)
            hook = api.SetWindowsHookExW(13, callback, kernel32.GetModuleHandleW(None), 0)
            if not hook:
                raise ctypes.WinError(ctypes.get_last_error())
            # Signal only after installation, not merely after thread startup.
            self.ready.set()
            while not self.cancelled.is_set():
                status = api.GetMessageW(ctypes.byref(message), None, 0, 0)
                if status == -1:
                    raise ctypes.WinError(ctypes.get_last_error())
                if status == 0:
                    break
        except Exception as exc:
            self.error = exc
        finally:
            if hook:
                self.user32.UnhookWindowsHookEx(hook)
            self.ready.set()

    def start(self):
        self.thread.start()
        if not self.ready.wait(2.0):
            raise RuntimeError('DD startup Windows-key guard timed out')
        if self.error is not None:
            raise RuntimeError('DD startup Windows-key guard failed') from self.error
        if not self.thread.is_alive():
            raise RuntimeError('DD startup Windows-key guard stopped')

    def stop(self):
        self.cancelled.set()
        if self.thread_id is not None:
            self.user32.PostThreadMessageW(self.thread_id, 0x12, 0, 0)
        self.thread.join(timeout=1.0)

    def key_state(self):
        if not hasattr(self, 'user32'):
            return None
        return {name: bool(self.user32.GetAsyncKeyState(vk) & 0x8000)
                for name, vk in (('lwin', 0x5B), ('rwin', 0x5C))}


@contextmanager
def suppress_start_menu():
    # Driver input need not carry LLKHF_INJECTED. Block both down and up.
    listener = _WinKeyHook()
    started = time.monotonic()
    try:
        listener.start()
        record('guard_started', settle_ms=SETTLE_SECONDS * 1000, keys=listener.key_state())
        yield
    finally:
        # HID events can arrive after DD_btn returns, including on failure/retry.
        threading.Event().wait(SETTLE_SECONDS)
        listener.stop()
        record('guard_stopped', suppressed=listener.suppressed,
               duration_ms=round((time.monotonic() - started) * 1000),
               keys=listener.key_state(), thread_alive=listener.thread.is_alive(),
               error=str(listener.error) if listener.error else None)
