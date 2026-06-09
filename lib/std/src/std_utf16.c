#include <stddef.h>
#include "utf16.h"

static size_t camp_utf16nlogicalsize(const utf16_int16_t *text, size_t len)
{
	return utf16nsize_lazy(text, len);
}

static int camp_utf16_is_low_surrogate(utf16_int16_t value)
{
	return 0xdc00 <= (utf16_int32_t)value && (utf16_int32_t)value <= 0xdfff;
}

static size_t camp_utf16_advance(const utf16_int16_t *text, size_t remaining)
{
	size_t size;

	if (remaining == 0 || text[0] == 0)
		return 0;

	size = utf16codepointcalcsize(text);
	if (size == 0 || size > remaining)
		return 1;

	return size;
}

const utf16_int16_t *camp_utf16nstr(
	const utf16_int16_t *haystack,
	size_t haystack_len,
	const utf16_int16_t *needle,
	size_t needle_len)
{
	size_t haystack_size = camp_utf16nlogicalsize(haystack, haystack_len);
	size_t needle_size = camp_utf16nlogicalsize(needle, needle_len);
	size_t offset = 0;

	if (needle_size == 0)
		return haystack;
	if (needle_size > haystack_size)
		return NULL;

	while (offset <= haystack_size - needle_size)
	{
		const utf16_int16_t *current = haystack + offset;
		size_t advance;

		if (!camp_utf16_is_low_surrogate(*current)
			&& utf16ncmp(current, needle, needle_size) == 0)
			return current;

		advance = camp_utf16_advance(current, haystack_size - offset);
		if (advance == 0)
			break;
		offset += advance;
	}

	return NULL;
}

const utf16_int16_t *camp_utf16ncasestr(
	const utf16_int16_t *haystack,
	size_t haystack_len,
	const utf16_int16_t *needle,
	size_t needle_len)
{
	size_t haystack_size = camp_utf16nlogicalsize(haystack, haystack_len);
	size_t needle_size = camp_utf16nlogicalsize(needle, needle_len);
	size_t offset = 0;

	if (needle_size == 0)
		return haystack;
	if (needle_size > haystack_size)
		return NULL;

	while (offset <= haystack_size - needle_size)
	{
		const utf16_int16_t *current = haystack + offset;
		size_t advance;

		if (!camp_utf16_is_low_surrogate(*current)
			&& utf16ncasecmp(current, needle, needle_size) == 0)
			return current;

		advance = camp_utf16_advance(current, haystack_size - offset);
		if (advance == 0)
			break;
		offset += advance;
	}

	return NULL;
}

const utf16_int16_t *camp_utf16nchr(
	const utf16_int16_t *text,
	size_t len,
	utf16_int32_t codepoint,
	int case_insensitive)
{
	size_t logical_size = camp_utf16nlogicalsize(text, len);
	size_t offset = 0;
	utf16_int32_t searched = case_insensitive ? utf16lwrcodepoint(codepoint) : codepoint;

	while (offset < logical_size)
	{
		const utf16_int16_t *current = text + offset;
		utf16_int32_t current_codepoint = 0;
		size_t advance = camp_utf16_advance(current, logical_size - offset);

		if (advance == 0)
			break;

		utf16codepoint(current, &current_codepoint);
		if ((case_insensitive ? utf16lwrcodepoint(current_codepoint) : current_codepoint) == searched)
			return current;

		offset += advance;
	}

	return NULL;
}

const utf16_int16_t *camp_utf16nrchr(
	const utf16_int16_t *text,
	size_t len,
	utf16_int32_t codepoint,
	int case_insensitive)
{
	size_t logical_size = camp_utf16nlogicalsize(text, len);
	size_t offset = 0;
	const utf16_int16_t *result = NULL;
	utf16_int32_t searched = case_insensitive ? utf16lwrcodepoint(codepoint) : codepoint;

	while (offset < logical_size)
	{
		const utf16_int16_t *current = text + offset;
		utf16_int32_t current_codepoint = 0;
		size_t advance = camp_utf16_advance(current, logical_size - offset);

		if (advance == 0)
			break;

		utf16codepoint(current, &current_codepoint);
		if ((case_insensitive ? utf16lwrcodepoint(current_codepoint) : current_codepoint) == searched)
			result = current;

		offset += advance;
	}

	return result;
}

const utf16_int16_t *camp_utf16npbrk_codepoints(
	const utf16_int16_t *text,
	size_t len,
	const utf16_int32_t *codepoints,
	size_t codepoint_count,
	int case_insensitive)
{
	size_t logical_size = camp_utf16nlogicalsize(text, len);
	size_t offset = 0;

	while (offset < logical_size)
	{
		const utf16_int16_t *current = text + offset;
		utf16_int32_t current_codepoint = 0;
		utf16_int32_t compared;
		size_t advance = camp_utf16_advance(current, logical_size - offset);
		size_t i;

		if (advance == 0)
			break;

		utf16codepoint(current, &current_codepoint);
		compared = case_insensitive ? utf16lwrcodepoint(current_codepoint) : current_codepoint;
		for (i = 0; i < codepoint_count; i++)
		{
			utf16_int32_t searched = case_insensitive ? utf16lwrcodepoint(codepoints[i]) : codepoints[i];
			if (compared == searched)
				return current;
		}

		offset += advance;
	}

	return NULL;
}

const utf16_int16_t *camp_utf16nrpbrk_codepoints(
	const utf16_int16_t *text,
	size_t len,
	const utf16_int32_t *codepoints,
	size_t codepoint_count,
	int case_insensitive)
{
	size_t logical_size = camp_utf16nlogicalsize(text, len);
	size_t offset = 0;
	const utf16_int16_t *result = NULL;

	while (offset < logical_size)
	{
		const utf16_int16_t *current = text + offset;
		utf16_int32_t current_codepoint = 0;
		utf16_int32_t compared;
		size_t advance = camp_utf16_advance(current, logical_size - offset);
		size_t i;

		if (advance == 0)
			break;

		utf16codepoint(current, &current_codepoint);
		compared = case_insensitive ? utf16lwrcodepoint(current_codepoint) : current_codepoint;
		for (i = 0; i < codepoint_count; i++)
		{
			utf16_int32_t searched = case_insensitive ? utf16lwrcodepoint(codepoints[i]) : codepoints[i];
			if (compared == searched)
			{
				result = current;
				break;
			}
		}

		offset += advance;
	}

	return result;
}
