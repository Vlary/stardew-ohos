cmake_minimum_required(VERSION 3.5.0)
project(stardew_entry)

set(SDL_HOME_PATH ${CMAKE_CURRENT_SOURCE_DIR}/../../../../sdl2-src)
set(NATIVE_INCLUDE_PATH ${SDL_HOME_PATH}/include
    ${SDL_HOME_PATH}/src/video/khronos/GLES2)
set(NATIVE_ENTRY_PATH ${CMAKE_CURRENT_SOURCE_DIR}/application)

add_subdirectory(${SDL_HOME_PATH} sdl2)
include_directories(${NATIVE_INCLUDE_PATH} ${NATIVE_ENTRY_PATH})
link_directories(${CMAKE_LIBRARY_OUTPUT_DIRECTORY})

add_library(entry SHARED application/napi_init.cpp)
add_dependencies(entry SDL2)

find_library(libnapi-lib ace_napi.z)
find_library(hilog-lib hilog_ndk.z)
target_link_libraries(entry PUBLIC libSDL2.so ${libnapi-lib} ${hilog-lib})
