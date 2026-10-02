#include <stdlib.h>
/*
 * Copyright (c) 2023 Huawei Device Co., Ltd.
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#include <hilog/log.h>
#include "gloable.h"

const unsigned int LOG_PRINT_DOMAIN = 0xFF00;
napi_ref g_rootView = nullptr;

napi_value setRootViewControl(napi_env env, napi_callback_info info)
{
    size_t argc = 1;
    napi_value argv[1];
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);
    napi_create_reference(env, argv[0], 1, &g_rootView);
    return nullptr;
}

/* ---- SDL 输入桥：把 ArkTS XComponent 的鼠标/键盘事件注入 SDL ---- */
extern "C" {
void SDL_OHOS_SetDensity(float density);
void SDL_OHOS_SendMouseMotion(float x_vp, float y_vp);
void SDL_OHOS_SendMouseButton(int down, int button);
void SDL_OHOS_SendMouseWheel(float dx, float dy);
void SDL_OHOS_SendKey(int down, int scancode);
void SDL_OHOS_SendText(const char *text);
}

/* 鸿蒙 KeyCode → SDL_Scancode（值基于 USB HID，见 SDL_scancode.h） */
static int hmKeyToSdl(int hm)
{
    if (hm >= 2017 && hm <= 2042) return 4 + (hm - 2017);            /* A..Z → 4..29 */
    if (hm >= 2000 && hm <= 2009) {                                   /* 0..9 */
        static const int d[10] = { 39, 30, 31, 32, 33, 34, 35, 36, 37, 38 };
        return d[hm - 2000];
    }
    if (hm >= 2090 && hm <= 2101) return 58 + (hm - 2090);           /* F1..F12 → 58..69 */
    switch (hm) {
        case 2043: return 54;   /* COMMA */
        case 2044: return 55;   /* PERIOD */
        case 2045: return 226;  /* ALT_LEFT */
        case 2046: return 230;  /* ALT_RIGHT */
        case 2047: return 225;  /* SHIFT_LEFT */
        case 2048: return 229;  /* SHIFT_RIGHT */
        case 2049: return 43;   /* TAB */
        case 2050: return 44;   /* SPACE */
        case 2054: return 40;   /* ENTER */
        case 2055: return 42;   /* DEL = Backspace */
        case 2056: return 53;   /* GRAVE */
        case 2057: return 45;   /* MINUS */
        case 2058: return 46;   /* EQUALS */
        case 2059: return 47;   /* LEFT_BRACKET */
        case 2060: return 48;   /* RIGHT_BRACKET */
        case 2061: return 49;   /* BACKSLASH */
        case 2062: return 51;   /* SEMICOLON */
        case 2063: return 52;   /* APOSTROPHE */
        case 2064: return 56;   /* SLASH */
        case 2066: return 46;   /* PLUS → EQUALS */
        case 2068: return 75;   /* PAGE_UP */
        case 2069: return 78;   /* PAGE_DOWN */
        case 2070: return 41;   /* ESCAPE */
        case 2071: return 76;   /* FORWARD_DEL */
        case 2072: return 224;  /* CTRL_LEFT */
        case 2073: return 228;  /* CTRL_RIGHT */
        case 2074: return 57;   /* CAPS_LOCK */
        case 2076: return 227;  /* META_LEFT */
        case 2077: return 231;  /* META_RIGHT */
        case 2081: return 74;   /* MOVE_HOME */
        case 2082: return 77;   /* MOVE_END */
        case 2083: return 73;   /* INSERT */
        case 2012: return 82;   /* DPAD_UP */
        case 2013: return 81;   /* DPAD_DOWN */
        case 2014: return 80;   /* DPAD_LEFT */
        case 2015: return 79;   /* DPAD_RIGHT */
        default: return 0;      /* SDL_SCANCODE_UNKNOWN */
    }
}

static napi_value SendMotion(napi_env env, napi_callback_info info)
{
    size_t argc = 2;
    napi_value argv[2];
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);
    double x = 0, y = 0;
    napi_get_value_double(env, argv[0], &x);
    napi_get_value_double(env, argv[1], &y);
    SDL_OHOS_SendMouseMotion((float)x, (float)y);
    return nullptr;
}

static napi_value SendButton(napi_env env, napi_callback_info info)
{
    size_t argc = 2;
    napi_value argv[2];
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);
    int32_t down = 0, btn = 0;
    napi_get_value_int32(env, argv[0], &down);
    napi_get_value_int32(env, argv[1], &btn);
    SDL_OHOS_SendMouseButton(down, btn);
    return nullptr;
}

static napi_value SendWheel(napi_env env, napi_callback_info info)
{
    size_t argc = 2;
    napi_value argv[2];
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);
    double dx = 0, dy = 0;
    napi_get_value_double(env, argv[0], &dx);
    napi_get_value_double(env, argv[1], &dy);
    SDL_OHOS_SendMouseWheel((float)dx, (float)dy);
    return nullptr;
}

static napi_value SendKey(napi_env env, napi_callback_info info)
{
    size_t argc = 2;
    napi_value argv[2];
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);
    int32_t down = 0, hm = 0;
    napi_get_value_int32(env, argv[0], &down);
    napi_get_value_int32(env, argv[1], &hm);
    int sc = hmKeyToSdl(hm);
    if (sc != 0) {
        SDL_OHOS_SendKey(down, sc);
    }
    return nullptr;
}

static napi_value SetDensity(napi_env env, napi_callback_info info)
{
    size_t argc = 1;
    napi_value argv[1];
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);
    double d = 1.0;
    napi_get_value_double(env, argv[0], &d);
    SDL_OHOS_SetDensity((float)d);
    return nullptr;
}


static napi_value SetGameDataDir(napi_env env, napi_callback_info info)
{
    size_t argc = 1;
    napi_value argv[1];
    napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr);
    char buf[512];
    size_t len = 0;
    napi_get_value_string_utf8(env, argv[0], buf, sizeof(buf), &len);
    setenv("SDV_BUNDLE_DIR", buf, 1);
    return nullptr;
}

EXTERN_C_START
static napi_value Init(napi_env env, napi_value exports)
{
    OH_LOG_Print(LOG_APP, LOG_INFO, LOG_PRINT_DOMAIN, "Init", "Init begins");
    if ((nullptr == env) || (nullptr == exports)) {
        OH_LOG_Print(LOG_APP, LOG_ERROR, LOG_PRINT_DOMAIN, "Init", "env or exports is null");
        return nullptr;
    }

    napi_property_descriptor desc[] = {
        { "setRootViewControl", nullptr, setRootViewControl, nullptr, nullptr, nullptr, napi_default, nullptr },
        { "setGameDataDir", nullptr, SetGameDataDir, nullptr, nullptr, nullptr, napi_default, nullptr },
        { "sendMouseMotion", nullptr, SendMotion, nullptr, nullptr, nullptr, napi_default, nullptr },
        { "sendMouseButton", nullptr, SendButton, nullptr, nullptr, nullptr, napi_default, nullptr },
        { "sendMouseWheel", nullptr, SendWheel, nullptr, nullptr, nullptr, napi_default, nullptr },
        { "sendKey", nullptr, SendKey, nullptr, nullptr, nullptr, napi_default, nullptr },
        { "setDensity", nullptr, SetDensity, nullptr, nullptr, nullptr, napi_default, nullptr },
    };
    if (napi_ok != napi_define_properties(env, exports, sizeof(desc) / sizeof(desc[0]), desc)) {
        OH_LOG_Print(LOG_APP, LOG_ERROR, LOG_PRINT_DOMAIN, "Init", "napi_define_properties failed");
        return nullptr;
    }
    return exports;
}
EXTERN_C_END

static napi_module nativerenderModule = {
    .nm_version = 1,
    .nm_flags = 0,
    .nm_filename = nullptr,
    .nm_register_func = Init,
    .nm_modname = "nativerender",
    .nm_priv = ((void *)0),
    .reserved = { 0 }
};

extern "C" __attribute__((constructor)) void RegisterModule(void)
{
    napi_module_register(&nativerenderModule);
}