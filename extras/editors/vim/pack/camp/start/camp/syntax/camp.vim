if exists('b:current_syntax')
  finish
endif

syntax keyword campKeyword if else for foreach while do switch case default break continue goto return yield try catch finally throw within new stackalloc delete await postpone
syntax keyword campKeyword using namespace as requires export internal public extern static virtual override sealed abstract fixed escaped scoped unscoped inline alias shadow unsafe
syntax keyword campKeyword class struct interface enum newtype params delegate fn iter once async this base default true false null
syntax keyword campKeyword const constof volatile in out thrown overload prep implements copyable sizeof vtableof typenameof caller sourceof configured classtype
syntax keyword campType any auto bool byte sbyte ushort short uint int ulong long nuint nint float double char wchar achar uchar void string wstring astring
syntax keyword campTestAttribute test testonly skip contained
syntax match campMetadata /@[A-Za-z_][A-Za-z0-9_]*/
syntax match campComment "//.*$"
syntax region campComment start="/\*" end="\*/"
syntax region campInterpolatedString start=/\$"/ skip=/\\"/ end=/"/ contains=campInterpolation,campEscapedBrace
syntax match campInterpolation /{[^{}]*}/ contained
syntax match campEscapedBrace /{{\|}}/ contained
syntax region campString start=/"/ skip=/\\"/ end=/"/
syntax region campChar start=/'/ skip=/\\'/ end=/'/
syntax match campNumber /\<[0-9][0-9A-Fa-f_xX]*\>/

highlight default link campKeyword Keyword
highlight default link campType Type
highlight default link campMetadata PreProc
highlight default link campTestAttribute PreProc
highlight default link campComment Comment
highlight default link campInterpolatedString String
highlight default link campInterpolation Identifier
highlight default link campEscapedBrace SpecialChar
highlight default link campString String
highlight default link campChar Character
highlight default link campNumber Number

let b:current_syntax = 'camp'
