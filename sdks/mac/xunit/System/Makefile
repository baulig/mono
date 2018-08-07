all:	MacTest.csproj

MacTest.csproj: ../../../ios/xunit/generator.pl MacTest.template martin-mac.sources MacTest.template
	-rm -f $@ output-tmp
	../../../ios/xunit/generator.pl MacTest.template martin-mac.sources output-tmp && mv output-tmp $@

clean:
	-rm -rf bin obj MacTest.csproj

build: MacTest.csproj
	msbuild

run: MacTest.csproj
	msbuild
	./launch.sh
