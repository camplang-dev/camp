if exists('b:current_syntax')
  finish
endif

syntax keyword campKeyword if else for foreach while do switch case default break continue return export import namespace try catch throw within finally new delete const static public private internal extern virtual override abstract async await yield this base
syntax keyword campType bool byte char double float int uint long ulong nint nuint short ushort string void auto
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
