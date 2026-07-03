// file: async_noawait_no_frame.c
#include "async_noawait_no_frame_private.h"

/* Private file declarations. */
static void doneAsync(void (* complete)(void *context), void *complete_context);

static void doneAsync(void (* complete)(void *context), void *complete_context)
{
	complete(complete_context);
	return;
}

// file: async_noawait_no_frame_private.h
#ifndef ASYNC_NOAWAIT_NO_FRAME_PRIVATE_H_
#define ASYNC_NOAWAIT_NO_FRAME_PRIVATE_H_

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
