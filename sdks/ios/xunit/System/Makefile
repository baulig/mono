all:	PhoneTest.csproj

PhoneTest.csproj: ../generator.pl PhoneTest.template martin-phone.sources PhoneTest.template
	-rm -f $@ output-tmp
	../generator.pl PhoneTest.template martin-phone.sources output-tmp && mv output-tmp $@

clean:
	-rm -rf bin obj simulator.log PhoneTest.csproj

build: PhoneTest.csproj
	msbuild

run: PhoneTest.csproj
	msbuild
	./launch.sh
