// file: async_resumption_stage1_noawait.c
#include "async_resumption_stage1_noawait_private.h"

/* Private file declarations. */
static void answerAsync(void (* complete)(void *context, int result), void *complete_context);

static void answerAsync(void (* complete)(void *context, int result), void *complete_context)
{
	complete(complete_context, 42);
	return;
}

// file: async_resumption_stage1_noawait_private.h
#ifndef ASYNC_RESUMPTION_STAGE1_NOAWAIT_PRIVATE_H_
#define ASYNC_RESUMPTION_STAGE1_NOAWAIT_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */

/* Object declarations. */


#endif
