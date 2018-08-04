thisdir = tools/pinvoke-checker
SUBDIRS =
include ../../build/rules.make

PROGRAM = pinvoke-checker.exe
NO_INSTALL = yes

API = $(filter basic build, $(PROFILE))
ifdef API
# It can be run using system .net during boostrap
TARGET_NET_REFERENCE = v4.6
# Trick to make it work during boostrap where it has to run with system
# assemblies not the ones in lib/basic folder
PROGRAM_USE_INTERMEDIATE_FILE = 1
endif

LIB_REFS = System Mono.Cecil

ifdef MCS_MODE
LIB_REFS += Mono.Cecil.Mdb
endif

include ../../build/executable.make

MARTIN_ARGS = \
	../../class/lib/monotouch/System.dll

martin::
	$(MAKE) V=1 PROFILE=basic
	mono --debug ../../class/lib/basic/pinvoke-checker.exe $(MARTIN_ARGS)
