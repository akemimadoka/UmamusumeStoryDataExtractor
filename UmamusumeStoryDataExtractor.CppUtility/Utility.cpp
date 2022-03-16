#include <utility>
#include <string_view>

#include <vcclr.h>

using namespace System;

namespace UmamusumeStoryDataExtractor
{
	public ref class CppUtility
	{
	public:
		static std::size_t GetCppStdHash(const String^ text)
		{
			const pin_ptr<const wchar_t> rawTextData = PtrToStringChars(text);
			return std::hash<std::wstring_view>{}(std::wstring_view(rawTextData));
		}
	};
}
