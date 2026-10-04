/* liblwjgl_lz4 shim —— 为星露谷 OHOS 端口提供 LWJGL lz4 native 符号。
 *
 * SDV 的 LWJGL.LZ4 类以 P/Invoke 调用 liblwjgl_lz4，入口符号为 LWJGL 生成的
 * Java_org_lwjgl_util_lz4_LZ4_* 形式（JNI 命名），调用时 env/clazz 恒为 0
 * （NativeAOT 无 JNI 环境），byte[] 参数由运行时按 pinned 指针传递。
 * 实现直接转发到标准 LZ4 API（lz4.c 静态编入）。
 */
#include "lz4.h"

#define EXPORT __attribute__((visibility("default")))

EXPORT int Java_org_lwjgl_util_lz4_LZ4_LZ4_1compressBound(void *env, void *clazz, int inputSize)
{
    (void)env; (void)clazz;
    return LZ4_compressBound(inputSize);
}

EXPORT int Java_org_lwjgl_util_lz4_LZ4_nLZ4_1compress_1default(
    void *env, void *clazz, const char *src, char *dest, int srcSize, int dstCapacity)
{
    (void)env; (void)clazz;
    return LZ4_compress_default(src, dest, srcSize, dstCapacity);
}

EXPORT int Java_org_lwjgl_util_lz4_LZ4_nLZ4_1decompress_1safe(
    void *env, void *clazz, const char *src, char *dest, int compressedSize, int dstCapacity)
{
    (void)env; (void)clazz;
    return LZ4_decompress_safe(src, dest, compressedSize, dstCapacity);
}
