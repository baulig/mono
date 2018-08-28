cmake_minimum_required (VERSION 2.8.10)

project (mono-native)

enable_language(C)
enable_language(CXX)

set (CMAKE_C_FLAGS "${CMAKE_C_FLAGS} ${C_CXX_FLAGS} ${MONO_NATIVE_CFLAGS} -mmacosx-version-min=10.12")
set (CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} ${C_CXX_FLAGS} ${MONO_NATIVE_CFLAGS}")
set (CMAKE_MACOSX_RPATH 1)
set (MONO_NATIVE 1)

set (COREFX_ROOT ${SRC_DIR}/../../external/corefx)

include_directories (
	${SRC_DIR}
	${COREFX_ROOT}/src/Native/Unix/Common
	${COREFX_ROOT}/src/Native/Unix/System.Security.Cryptography.Native.Apple
)

set (
	MONO_NATIVE_SOURCES

	martin-test.c
	${COREFX_ROOT}/src/Native/Unix/System.Security.Cryptography.Native.Apple/pal_hmac.c
	${COREFX_ROOT}/src/Native/Unix/System.Security.Cryptography.Native.Apple/pal_hmac.h
)

add_library (apple-crypto-native SHARED ${MONO_NATIVE_SOURCES})
