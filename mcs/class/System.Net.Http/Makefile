thisdir = class/System.Net.Http
SUBDIRS = 
include ../../build/rules.make

LIBRARY = System.Net.Http.dll

ifndef NO_MONO_SECURITY
MONO_SECURITY = Mono.Security
endif

RESX_RESOURCE_STRING = \
        ../../../external/corefx/src/System.Net.Requests/src/Resources/Strings.resx \
        ../../../external/corefx/src/System.Net.Http/src/Resources/Strings.resx

LIB_REFS = System.Core System $(MONO_SECURITY)
LIB_MCS_FLAGS = $(EXTRA_LIB_MCS_FLAGS)
ifeq (monodroid,$(PROFILE))
LIB_MCS_FLAGS += -d:XAMARIN_MODERN
endif

LIB_MCS_FLAGS += -d:MARTIN_TEST -d:NET46 -nowarn:436 -unsafe

TEST_LIB_REFS = System System.Core
TEST_MCS_FLAGS =

include ../../build/library.make
