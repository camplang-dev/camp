#include <stddef.h>
#include "utf8.h"

static size_t camp_utf8nlogicalsize(const utf8_int8_t *text, size_t len)
{
	return utf8nsize_lazy(text, len);
}

static int camp_utf8_is_continuation(utf8_int8_t value)
{
	return (0x80 == (0xc0 & value));
}

static size_t camp_utf8_advance(const utf8_int8_t *text, size_t remaining)
{
	size_t size;

	if (remaining == 0 || text[0] == '\0')
		return 0;

	size = utf8codepointcalcsize(text);
	if (size == 0 || size > remaining)
		return 1;

	return size;
}

const utf8_int8_t *camp_utf8nstr(
	const utf8_int8_t *haystack,
	size_t haystack_len,
	const utf8_int8_t *needle,
	size_t needle_len)
{
	size_t haystack_size = camp_utf8nlogicalsize(haystack, haystack_len);
	size_t needle_size = camp_utf8nlogicalsize(needle, needle_len);
	size_t offset = 0;

	if (needle_size == 0)
		return haystack;
	if (needle_size > haystack_size)
		return NULL;

	while (offset <= haystack_size - needle_size)
	{
		const utf8_int8_t *current = haystack + offset;
		size_t advance;

		if (!camp_utf8_is_continuation(*current)
			&& utf8ncmp(current, needle, needle_size) == 0)
			return current;

		advance = camp_utf8_advance(current, haystack_size - offset);
		if (advance == 0)
			break;
		offset += advance;
	}

	return NULL;
}

const utf8_int8_t *camp_utf8ncasestr(
	const utf8_int8_t *haystack,
	size_t haystack_len,
	const utf8_int8_t *needle,
	size_t needle_len)
{
	size_t haystack_size = camp_utf8nlogicalsize(haystack, haystack_len);
	size_t needle_size = camp_utf8nlogicalsize(needle, needle_len);
	size_t offset = 0;

	if (needle_size == 0)
		return haystack;
	if (needle_size > haystack_size)
		return NULL;

	while (offset <= haystack_size - needle_size)
	{
		const utf8_int8_t *current = haystack + offset;
		size_t advance;

		if (!camp_utf8_is_continuation(*current)
			&& utf8ncasecmp(current, needle, needle_size) == 0)
			return current;

		advance = camp_utf8_advance(current, haystack_size - offset);
		if (advance == 0)
			break;
		offset += advance;
	}

	return NULL;
}

const utf8_int8_t *camp_utf8nchr(
	const utf8_int8_t *text,
	size_t len,
	utf8_int32_t codepoint,
	int case_insensitive)
{
	size_t logical_size = camp_utf8nlogicalsize(text, len);
	size_t offset = 0;
	utf8_int32_t searched = case_insensitive ? utf8lwrcodepoint(codepoint) : codepoint;

	while (offset < logical_size)
	{
		const utf8_int8_t *current = text + offset;
		utf8_int32_t current_codepoint = 0;
		size_t advance = camp_utf8_advance(current, logical_size - offset);

		if (advance == 0)
			break;

		utf8codepoint(current, &current_codepoint);
		if ((case_insensitive ? utf8lwrcodepoint(current_codepoint) : current_codepoint) == searched)
			return current;

		offset += advance;
	}

	return NULL;
}

const utf8_int8_t *camp_utf8nrchr(
	const utf8_int8_t *text,
	size_t len,
	utf8_int32_t codepoint,
	int case_insensitive)
{
	size_t logical_size = camp_utf8nlogicalsize(text, len);
	size_t offset = 0;
	const utf8_int8_t *result = NULL;
	utf8_int32_t searched = case_insensitive ? utf8lwrcodepoint(codepoint) : codepoint;

	while (offset < logical_size)
	{
		const utf8_int8_t *current = text + offset;
		utf8_int32_t current_codepoint = 0;
		size_t advance = camp_utf8_advance(current, logical_size - offset);

		if (advance == 0)
			break;

		utf8codepoint(current, &current_codepoint);
		if ((case_insensitive ? utf8lwrcodepoint(current_codepoint) : current_codepoint) == searched)
			result = current;

		offset += advance;
	}

	return result;
}

const utf8_int8_t *camp_utf8npbrk_codepoints(
	const utf8_int8_t *text,
	size_t len,
	const utf8_int32_t *codepoints,
	size_t codepoint_count,
	int case_insensitive)
{
	size_t logical_size = camp_utf8nlogicalsize(text, len);
	size_t offset = 0;

	while (offset < logical_size)
	{
		const utf8_int8_t *current = text + offset;
		utf8_int32_t current_codepoint = 0;
		utf8_int32_t compared;
		size_t advance = camp_utf8_advance(current, logical_size - offset);
		size_t i;

		if (advance == 0)
			break;

		utf8codepoint(current, &current_codepoint);
		compared = case_insensitive ? utf8lwrcodepoint(current_codepoint) : current_codepoint;
		for (i = 0; i < codepoint_count; i++)
		{
			utf8_int32_t searched = case_insensitive ? utf8lwrcodepoint(codepoints[i]) : codepoints[i];
			if (compared == searched)
				return current;
		}

		offset += advance;
	}

	return NULL;
}

const utf8_int8_t *camp_utf8nrpbrk_codepoints(
	const utf8_int8_t *text,
	size_t len,
	const utf8_int32_t *codepoints,
	size_t codepoint_count,
	int case_insensitive)
{
	size_t logical_size = camp_utf8nlogicalsize(text, len);
	size_t offset = 0;
	const utf8_int8_t *result = NULL;

	while (offset < logical_size)
	{
		const utf8_int8_t *current = text + offset;
		utf8_int32_t current_codepoint = 0;
		utf8_int32_t compared;
		size_t advance = camp_utf8_advance(current, logical_size - offset);
		size_t i;

		if (advance == 0)
			break;

		utf8codepoint(current, &current_codepoint);
		compared = case_insensitive ? utf8lwrcodepoint(current_codepoint) : current_codepoint;
		for (i = 0; i < codepoint_count; i++)
		{
			utf8_int32_t searched = case_insensitive ? utf8lwrcodepoint(codepoints[i]) : codepoints[i];
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
