#! -*- makefile -*-

BOOTSTRAP_PROFILE = build

BOOTSTRAP_MCS = MONO_PATH="$(topdir)/class/lib/$(BOOTSTRAP_PROFILE)$(PLATFORM_PATH_SEPARATOR)$$MONO_PATH" $(INTERNAL_CSC)
MCS = $(BOOTSTRAP_MCS)

PLATFORMS = darwin linux win32

profile-check:
	@:

DEFAULT_REFERENCES = mscorlib

PROFILE_MCS_FLAGS = \
	-d:NET_1_1 \
	-d:NET_2_0 \
	-d:NET_2_1 \
	-d:MOBILE \
	-d:MOBILE_DYNAMIC \
	-d:NET_3_5 \
	-d:NET_4_0 \
	-d:NET_4_5 \
	-d:MONO \
	-nowarn:1699 \
	-nostdlib \
	$(PLATFORM_DEBUG_FLAGS)

API_BIN_PROFILE = build/monotouch
FRAMEWORK_VERSION = 2.1

NO_INSTALL = yes
MOBILE_DYNAMIC = yes
MOBILE_PROFILE = yes
NO_CONSOLE = yes
