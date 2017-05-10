thisdir = class/System.Net.Http
SUBDIRS = 
include ../../build/rules.make

LIBRARY = System.Net.Http.dll

ifndef NO_MONO_SECURITY
MONO_SECURITY = Mono.Security
endif

LIB_REFS = System.Core System $(MONO_SECURITY)
LIB_MCS_FLAGS = $(EXTRA_LIB_MCS_FLAGS)
ifeq (monodroid,$(PROFILE))
LIB_MCS_FLAGS += -d:XAMARIN_MODERN
endif

TEST_LIB_REFS = System System.Core
TEST_MCS_FLAGS =

include ../../build/library.make
