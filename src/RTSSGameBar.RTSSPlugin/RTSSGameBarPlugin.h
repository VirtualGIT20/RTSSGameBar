#pragma once

#include <Windows.h>

#ifdef RTSSGAMEBAR_PLUGIN_EXPORTS
#define RTSSGAMEBAR_PLUGIN_API extern "C" __declspec(dllexport)
#else
#define RTSSGAMEBAR_PLUGIN_API extern "C" __declspec(dllimport)
#endif

RTSSGAMEBAR_PLUGIN_API BOOL Start();
RTSSGAMEBAR_PLUGIN_API void Stop();
RTSSGAMEBAR_PLUGIN_API BOOL Setup(HWND hWnd);
